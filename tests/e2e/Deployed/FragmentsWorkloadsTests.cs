using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// O2's and O4's drives (<see cref="FragmentsWorkloads"/>) with no AWS and no browser: the AWS CLI's
/// answers recorded, the sessions answered by a double that hands each step a fresh trace, the clock
/// moved by the drive's own waits, and the lines a recorded capture. Each test names in its body the
/// mutation that turns it red.
/// </summary>
public sealed partial class FragmentsWorkloadsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = FragmentsObserverTests.SignedIn.AddSeconds(30);

    private static readonly IReadOnlyList<LiveFragment> Fragments =
        [.. new[] { "orders", "profile", "alerts", "reports", "history", "archive" }.Select(id => new LiveFragment(id, "f1", "entry.js", [id]))];

    /// <summary>How long a sign-in through the chain takes here.</summary>
    private static readonly TimeSpan SignInTakes = TimeSpan.FromSeconds(20);

    /// <summary>The sessions: each sign-in a step at the clock's time with a session of its own, each call answered 200.</summary>
    private sealed class Sessions(ManualClock clock) : IFragmentsSessions
    {
        public List<(Step Step, string Session)> SignIns { get; } = [];

        public List<(Step Step, string Path, string Session)> Calls { get; } = [];

        public Func<string, Exception?> Fail { get; init; } = _ => null;

        public Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct)
        {
            if (Fail(name) is { } failure)
            {
                throw failure;
            }

            Step step = new(name, ClientTrace.Fresh().TraceId, clock.GetUtcNow(), 302) { Route = "bff" };
            clock.Advance(SignInTakes);
            string session = $"s{SignIns.Count + 1:D2}";
            SignIns.Add((step, session));
            return Task.FromResult<(Step, SessionCookies?)>((step, new SessionCookies(session)));
        }

        public Task<Step> SendAsync(string name, string path, SessionCookies cookies, CancellationToken ct)
        {
            Step step = new(name, ClientTrace.Fresh().TraceId, clock.GetUtcNow(), 200) { Route = "api" };
            lock (Calls)
            {
                Calls.Add((step, path, cookies.Reveal()));
            }

            return Task.FromResult(step);
        }

        public Task<IReadOnlyList<Step>> OverlapAsync(string name, string fallbackFragment, CancellationToken ct)
        {
            Step signIn = new(name + "-sign-in", ClientTrace.Fresh().TraceId, clock.GetUtcNow(), 302) { Route = "bff" };
            Step fallback = new(name + "-fallback", ClientTrace.Fresh().TraceId, clock.GetUtcNow(), 302) { Route = "bff" };
            SignIns.Add((signIn, "overlap"));
            SignIns.Add((fallback, "overlap"));
            return Task.FromResult<IReadOnlyList<Step>>([signIn, fallback]);
        }
    }

    /// <summary>The ECS and CloudWatch reads a drive sends, answered from the recording; the service starts at three tasks, a count no topology the drive scales to.</summary>
    private static RecordedRunner Aws(List<CapacityPeriod>? periods = null) => new(arguments => arguments switch
    {
        ["ecs", "describe-services", ..] => """{ "services": [ { "desiredCount": 3 } ] }""",
        ["ecs", "update-service", ..] => "{}",
        ["ecs", "wait", "services-stable", ..] => "",
        ["cloudwatch", "get-metric-statistics", ..] => new JsonObject
        {
            ["Datapoints"] = new JsonArray([.. (periods ?? []).Select(period => new JsonObject { ["Timestamp"] = period.Start.ToString("O"), ["Sum"] = period.Sum })]),
        }.ToJsonString(),
        _ => throw new InvalidOperationException($"unexpected aws {string.Join(' ', arguments)}"),
    });

    private static string Scaled(string[] command) => command is ["ecs", "update-service", .., "--desired-count", string count, "--output", "json", "--region", _] ? count : "";

    private static async Task<(RefreshContention Record, IReadOnlyList<Step> Steps, Sessions Sessions, RecordedRunner Aws)> ContentionAsync(Func<string, Exception?>? fail = null)
    {
        ManualClock clock = new(Start);
        Sessions sessions = new(clock) { Fail = fail ?? (_ => null) };
        RecordedRunner aws = Aws();
        FragmentsWorkloads workloads = new(FragmentsObserverTests.Cell, aws, sessions) { Clock = clock, Delay = Moves(clock) };
        (RefreshContention record, IReadOnlyList<Step> steps) = await workloads.ContentionAsync(Fragments, Ct);
        return (record, steps, sessions, aws);
    }

    /// <summary>The lines the cell writes for the drive: a request line per step on its session, a refresh write per call, and the overlap's two fragment writes.</summary>
    private static JsonObject[] ContentionLines(Sessions sessions, int attempts = 1, string outcome = "written")
    {
        List<JsonObject> lines = [];
        foreach ((Step step, string session) in sessions.SignIns)
        {
            lines.Add(FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, step.At, FragmentsObserverTests.RequestLine(step, sessionRef: session)));
        }

        int i = 0;
        foreach ((Step step, string _, string session) in sessions.Calls)
        {
            string task = i++ % 2 == 0 ? FragmentsObserverTests.TaskA : FragmentsObserverTests.TaskB;
            lines.Add(FragmentsObserverTests.Line(task, step.At, FragmentsObserverTests.RequestLine(step, "api", session)));
            lines.Add(FragmentsObserverTests.Line(task, step.At, new JsonObject
            {
                ["msg"] = $"Refresh write for app shell, fragment orders, session {session}: ended {outcome} after {attempts} versioned replaces.",
                ["session_ref"] = session,
            }));
        }

        DateTimeOffset at = sessions.SignIns[^1].Step.At;
        lines.Add(FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, at, new JsonObject { ["msg"] = "Fragment sign-in for app shell, fragment orders, session overlap: the chain write ended written after 1 versioned replaces.", ["session_ref"] = "overlap" }));
        lines.Add(FragmentsObserverTests.Line(FragmentsObserverTests.TaskB, at, new JsonObject { ["msg"] = "Fragment sign-in for app shell, fragment archive, session overlap: the fallback write ended written after 2 versioned replaces.", ["session_ref"] = "overlap" }));
        return [.. lines];
    }

    [Fact]
    public async Task O2_scales_to_one_task_then_two_samples_each_and_puts_the_service_back()
    {
        // Mutation: dropping the restore from FragmentsWorkloads.ContentionAsync (ScaleAsync(desired, restore)) leaves the
        // service at the drive's last count, and restoring to a fixed 2 (ScaleAsync(2, restore)) in place of the count
        // read puts it at the wrong one; each fails the second assertion.
        (RefreshContention record, IReadOnlyList<Step> steps, Sessions sessions, RecordedRunner aws) = await ContentionAsync();

        string[][] commands = [.. aws.Calls.Select(call => call.Arguments)];
        Assert.Equal(["ecs", "describe-services", "--cluster", FragmentsObserverTests.Cell.Cluster, "--services", FragmentsObserverTests.Cell.Service, "--output", "json", "--region", FragmentsObserverTests.Cell.Region], commands[0]);
        Assert.Equal(["1", "2", "3"], commands.Select(Scaled).Where(count => count.Length > 0));
        Assert.Equal(3, commands.Count(command => command is ["ecs", "wait", "services-stable", ..]));
        Assert.Equal("3", Scaled(commands[^2]));
    }

    [Fact]
    public async Task O2s_sample_calls_each_sessions_six_sets_at_once_after_they_are_due_and_passes_with_its_writes()
    {
        // Mutation: dropping the wait for DueAfter from FragmentsWorkloads.SampleAsync sends the calls at the sign-ins'
        // time and fails the second assertion; reading writes from the wrong topology's steps fails the verdict.
        (RefreshContention record, IReadOnlyList<Step> steps, Sessions sessions, _) = await ContentionAsync();

        Assert.Equal(2 * FragmentsWorkloads.Repetitions * FragmentsObserver.DueSets, sessions.Calls.Count);
        DateTimeOffset lastSignIn = sessions.SignIns.Where(each => each.Step.Name.StartsWith("o2-t1-", StringComparison.Ordinal)).Max(each => each.Step.At);
        Assert.All(sessions.Calls.Where(call => call.Step.Name.StartsWith("o2-t1-", StringComparison.Ordinal)), call => Assert.True(call.Step.At >= lastSignIn + FragmentsWorkloads.DueAfter));
        Assert.Equal(
            new[] { "/api/orders/whoami", "/fragments/orders/api/orders/whoami", "/fragments/profile/api/profile/whoami", "/fragments/alerts/api/alerts/whoami", "/fragments/reports/api/reports/whoami", "/fragments/history/api/history/whoami" }.Order(StringComparer.Ordinal),
            sessions.Calls.Where(call => call.Step.Name.StartsWith("o2-t1-r1-", StringComparison.Ordinal)).Select(call => call.Path).Order(StringComparer.Ordinal));
        Assert.All(sessions.Calls.GroupBy(call => call.Step.Name[..call.Step.Name.LastIndexOf('-')]), round => Assert.Single(round.Select(call => call.Session).Distinct()));

        GatewayCapture capture = await FragmentsObserverTests.CaptureAsync(ContentionLines(sessions));
        RefreshContention written = FragmentsWorkloads.WithWrites(record, steps, capture);
        RowResult result = FragmentsObserver.Contention(written);
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.All(written.Topologies, sample => Assert.Equal(FragmentsWorkloads.Repetitions * FragmentsObserver.DueSets, sample.Writes!.Count));
        Assert.Equal([FragmentsObserverTests.TaskA, FragmentsObserverTests.TaskB], written.Callbacks!.TasksWrote);
        Assert.Equal([1, 2], written.Callbacks.AttemptsPerWrite);
    }

    [Fact]
    public async Task O2s_writes_count_each_lost_replace_as_a_conflict_and_an_exhausted_budget()
    {
        // Mutation: counting conflicts as attempts - 1 whatever the outcome (FragmentsLines.Count) gives 2 here and fails this test.
        (RefreshContention record, IReadOnlyList<Step> steps, Sessions sessions, _) = await ContentionAsync();

        RefreshContention written = FragmentsWorkloads.WithWrites(record, steps, await FragmentsObserverTests.CaptureAsync(ContentionLines(sessions, 3, "exhausted")));

        Assert.All(written.Topologies.SelectMany(sample => sample.Writes!), write => Assert.Equal(new WriteCount(3, 3, true), write));
        Assert.Contains("budget(s) exhausted", FragmentsObserver.Contention(written).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O2_with_no_write_lines_is_not_proved()
    {
        // Mutation: making WithWrites answer one WriteCount per timed call when the lines hold none passes the observer
        // here, where the cell recorded no write, and fails this test.
        (RefreshContention record, IReadOnlyList<Step> steps, Sessions sessions, _) = await ContentionAsync();
        JsonObject[] requests = [.. ContentionLines(sessions).Where(line => !line["message"]!.GetValue<string>().Contains("versioned replaces", StringComparison.Ordinal))];

        RowResult result = FragmentsObserver.Contention(FragmentsWorkloads.WithWrites(record, steps, await FragmentsObserverTests.CaptureAsync(requests)));

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("per-write counts", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O2_puts_the_service_back_when_a_sign_in_throws()
    {
        // Mutation: Restoring.RunAsync rethrowing before its restore leaves the last command a scale to 1 and fails this test.
        TimeoutException failure = new("the provider's page did not load");
        ManualClock clock = new(Start);
        Sessions sessions = new(clock) { Fail = name => name == "o2-t1-session-2" ? failure : null };
        RecordedRunner aws = Aws();
        FragmentsWorkloads workloads = new(FragmentsObserverTests.Cell, aws, sessions) { Clock = clock, Delay = Moves(clock) };

        await Assert.ThrowsAsync<TimeoutException>(() => workloads.ContentionAsync(Fragments, Ct));

        string[][] commands = [.. aws.Calls.Select(call => call.Arguments)];
        Assert.Equal(["1", "3"], commands.Select(Scaled).Where(count => count.Length > 0));
        Assert.Equal("3", Scaled(commands[^2]));
    }

    private static async Task<(CapacitySample Sample, IReadOnlyList<Step> Steps, DateTimeOffset Until, RecordedRunner Aws)> CapacityAsync(DateTimeOffset start)
    {
        ManualClock clock = new(start);
        Sessions sessions = new(clock);
        DateTimeOffset first = FragmentsWorkloads.Minute(start).AddMinutes(2);
        List<CapacityPeriod> periods = [new(first.AddMinutes(-1), 0), new(first, 9), new(first.AddMinutes(1), 6), new(first.AddMinutes(2), 0)];
        RecordedRunner aws = Aws(periods);
        FragmentsWorkloads workloads = new(FragmentsObserverTests.Cell, aws, sessions) { Clock = clock, Delay = Moves(clock) };
        (CapacitySample sample, IReadOnlyList<Step> steps, DateTimeOffset until) =
            await workloads.CapacityAsync(new FragmentsReaders(FragmentsObserverTests.Cell, aws), "isolated-table", Ct);
        return (sample, steps, until, aws);
    }

    private static JsonObject[] CapacityLines(IReadOnlyList<Step> steps) =>
        [.. steps.Select((step, i) => FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, step.At, FragmentsObserverTests.RequestLine(step, sessionRef: $"s{i + 1:D2}")))];

    [Fact]
    public async Task O4_starts_on_a_minute_after_a_whole_quiet_one_and_reads_the_periods_from_control_to_control()
    {
        // Mutation: starting the workload at the next minute (AddMinutes(1) in FragmentsWorkloads.CapacityAsync) puts the
        // control minute before the drive began and fails the first assertion.
        (CapacitySample sample, IReadOnlyList<Step> steps, DateTimeOffset until, RecordedRunner aws) = await CapacityAsync(Start);

        Assert.True(FragmentsWorkloads.Minute(sample.Start).AddMinutes(-1) >= Start, "the control minute began before the drive");
        Assert.Equal(FragmentsWorkloads.SignIns, sample.Operations);
        Assert.Equal(FragmentsWorkloads.Minute(sample.Stop).AddMinutes(2), until);
        string[] read = Assert.Single(aws.Calls, call => call.Arguments is ["cloudwatch", ..]).Arguments;
        Assert.Equal(["cloudwatch", "get-metric-statistics", "--namespace", "AWS/DynamoDB", "--metric-name", "ConsumedWriteCapacityUnits",
            "--dimensions", "Name=TableName,Value=isolated-table",
            "--start-time", FragmentsWorkloads.Minute(sample.Start).AddMinutes(-1).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            "--end-time", until.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), "--period", "60", "--statistics", "Sum",
            "--output", "json", "--region", FragmentsObserverTests.Cell.Region], read);

        CapacitySample isolated = FragmentsWorkloads.Isolated(sample, steps, await FragmentsObserverTests.CaptureAsync(CapacityLines(steps)));
        RowResult result = FragmentsObserver.Size(new RecordSize(new ItemSize(369, 300), isolated));
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.Empty(isolated.OtherWriters!);
        Assert.Empty(isolated.OverlappingWorkloads!);
    }

    [Fact]
    public async Task O4s_isolation_names_a_request_and_a_session_the_workload_did_not_make()
    {
        // Mutation: dropping the window's lower bound from FragmentsLines.Foreign counts the line two minutes before the
        // workload and fails the second assertion; dropping OtherSessions' own-session filter fails the first.
        (CapacitySample sample, IReadOnlyList<Step> steps, _, _) = await CapacityAsync(Start);
        Step stray = new("someone", ClientTrace.Fresh().TraceId, sample.Start.AddSeconds(5), 200);
        Step before = new("control", ClientTrace.Fresh().TraceId, FragmentsWorkloads.Minute(sample.Start).AddMinutes(-2), 200);
        JsonObject[] lines =
        [
            .. CapacityLines(steps),
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskB, stray.At, FragmentsObserverTests.RequestLine(stray, "api", "other")),
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskB, before.At, FragmentsObserverTests.RequestLine(before, "health")),
        ];

        CapacitySample isolated = FragmentsWorkloads.Isolated(sample, steps, await FragmentsObserverTests.CaptureAsync(lines));

        Assert.Equal(["session other"], isolated.OtherWriters);
        Assert.Equal([$"trace {stray.TraceId} route api on {FragmentsObserverTests.TaskB}"], isolated.OverlappingWorkloads);
        Assert.Equal(Verdict.NotProved, FragmentsObserver.Size(new RecordSize(new ItemSize(369, 300), isolated)).Verdict);
    }

    [Fact]
    public void The_write_lines_are_read_in_the_wording_the_identity_project_writes()
    {
        // Mutation: changing FragmentsLines' patterns to "after {N} replaces" fails the parses below; a change to any of
        // the three LoggerMessages' wording fails the source checks.
        string source = Joined(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Platform.Identity", "AccessTokenSource.cs")));
        string events = Joined(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Platform.Identity", "FragmentEvents.cs")));
        string fragmentRefresh = FragmentsLines.RefreshWritePrefix + "{AppId}, fragment {FragmentId}, session {SessionRef}: ended {Outcome} after {StoreAttempts} versioned replaces.";
        string refresh = FragmentsLines.RefreshWritePrefix + "{AppId}, session {SessionRef}: ended {Outcome} after {StoreAttempts} versioned replaces.";
        string fragmentWrite = FragmentsLines.FragmentWritePrefix + "{AppId}, fragment {FragmentId}, session {SessionRef}: the {Writer} write ended {Outcome} after {StoreAttempts} versioned replaces.";
        Assert.Contains("\"" + fragmentRefresh + "\"", source, StringComparison.Ordinal);
        Assert.Contains("\"" + refresh + "\"", source, StringComparison.Ordinal);
        Assert.Contains("\"" + fragmentWrite + "\"", events, StringComparison.Ordinal);
        Assert.Contains($"LogWrite(refresh, \"{FragmentsLines.Exhausted}\", attempts)", source, StringComparison.Ordinal);
        Assert.Contains($"LogWrite(refresh, \"{FragmentsLines.Written}\", attempts)", source, StringComparison.Ordinal);

        // Each template rendered as the gateway writes it, then read back by FragmentsLines.
        Assert.Equal(new WriteCount(3, 3, true), FragmentsLines.RefreshWrite(Rendered(fragmentRefresh, FragmentsLines.Exhausted)));
        Assert.Equal(new WriteCount(2, 1, false), FragmentsLines.RefreshWrite(Rendered(refresh, FragmentsLines.Written, attempts: 2)));
        Assert.Equal(("fallback", 2), FragmentsLines.FragmentWrite(Rendered(fragmentWrite, FragmentsLines.Written, attempts: 2)));
    }

    /// <summary>A gateway line whose <c>msg</c> is <paramref name="template"/> with its placeholders filled, as the logger fills them.</summary>
    private static GatewayEvent Rendered(string template, string outcome, int attempts = 3)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["AppId"] = "shell",
            ["FragmentId"] = "orders",
            ["SessionRef"] = "s01",
            ["Writer"] = "fallback",
            ["Outcome"] = outcome,
            ["StoreAttempts"] = attempts.ToString(CultureInfo.InvariantCulture),
        };
        string msg = Placeholder().Replace(template, match => values[match.Groups["name"].Value]);
        using JsonDocument line = JsonDocument.Parse(new JsonObject { ["msg"] = msg, ["session_ref"] = "s01" }.ToJsonString());
        return new GatewayEvent("gateway/gateway/" + FragmentsObserverTests.TaskA, FragmentsObserverTests.TaskA, Start, Start, line.RootElement.Clone());
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)\}", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Placeholder();

    /// <summary>The drive's delay as the clock's move: nothing waits, and the clock says the wait passed.</summary>
    private static Func<TimeSpan, CancellationToken, Task> Moves(ManualClock clock) => (by, _) =>
    {
        if (by > TimeSpan.Zero)
        {
            clock.Advance(by);
        }

        return Task.CompletedTask;
    };

    /// <summary>A C# source with each <c>"…" + "…"</c> joined, as the compiler joins it.</summary>
    private static string Joined(string source) => Concatenation().Replace(source, "");

    [GeneratedRegex("\"\\s*\\+\\s*\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Concatenation();
}
