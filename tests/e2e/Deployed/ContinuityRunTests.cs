using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// What the continuity class does around the observer (#358), with no AWS: the evidence file and its
/// rerun budget (Q142, Q148), the run loop's bounds and control (Q143, Q144), row 4's lever and its
/// refusal (Q145), the local stack's skip (Q139), and that no cookie or ticket reaches an output or an
/// evidence file (N-16's canaries).
/// </summary>
public sealed class ContinuityRunTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Cell Cell => Cell.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")));

    /// <summary>N-16's kind of canary: a value that must appear in nothing the class prints or writes.</summary>
    private const string Canary = "CfDJ8canary-session-ticket-0123456789";

    private readonly string directory = Directory.CreateTempSubdirectory("spa-e2e-continuity-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private const string TaskA = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";
    private const string TaskB = "9f8e7d6c5b4a43210fedcba987654321";

    /// <summary>
    /// A runner standing in for git and the AWS CLI: <c>git check-ignore</c> answers <paramref name="ignored"/>,
    /// and <c>logs filter-log-events</c> answers the request lines <paramref name="lines"/> gives, read when asked.
    /// </summary>
    private static RecordedRunner Runner(bool ignored, Func<JsonArray> lines) => RecordedRunner.WithExits(command => command switch
    {
        { Program: "git", Arguments: [.., "check-ignore", "-q", "--", _] } => new CommandResult(ignored ? 0 : 1, ""),
        { Program: "aws", Arguments: ["logs", "filter-log-events", ..] } => new CommandResult(0, new JsonObject { ["events"] = lines() }.ToJsonString()),
        _ => throw new InvalidOperationException($"unexpected {command.Program} {string.Join(' ', command.Arguments)}"),
    });

    private static JsonObject Line(string task, Step step) => new()
    {
        ["logStreamName"] = "gateway/gateway/" + task,
        ["timestamp"] = step.At.ToUnixTimeMilliseconds(),
        ["ingestionTime"] = step.At.AddSeconds(3).ToUnixTimeMilliseconds(),
        ["message"] = new JsonObject { ["msg"] = "request", ["trace_id"] = step.TraceId, ["route"] = step.Route ?? "bff", ["session_ref"] = "5e5510a0f00d" }.ToJsonString(),
    };

    private static Step Sent(string name, int? status = 200, string? route = null) =>
        new(name, ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, status) { Expect = 200, Route = route };

    private ContinuityRun Run(RecordedRunner runner) =>
        new(new ContinuityEvidence(directory, runner), new ContinuityLog(Cell, runner)) { Delivery = TimeSpan.Zero };

    [Fact]
    public async Task A_rerun_reads_the_earlier_evidence_and_spends_only_the_attempts_left()
    {
        // The recorded earlier file: seven attempts spent, all on one task.
        RowEvidence earlier = new("row3", 7, 300, Verdict.NotProved, "no attempt of 7 placed session-1 and session-2 on two different tasks", [], []);
        await File.WriteAllTextAsync(Path.Combine(directory, "row3.json"), JsonSerializer.Serialize(earlier), Ct);

        JsonArray lines = [];
        Step control = Sent("control", route: "health");
        lines.Add(Line(TaskA, control));
        RecordedRunner runner = Runner(ignored: true, () => (JsonArray)lines.DeepClone());
        int made = 0;

        RowResult result = await Run(runner).TwoTaskRowAsync("row3", control, null,
            (n, _) =>
            {
                made++;
                Attempt attempt = new(n, [Sent("session-1"), Sent("session-2")]);
                lines.Add(Line(TaskA, attempt.Steps[0]));
                lines.Add(Line(TaskA, attempt.Steps[1]));
                return Task.FromResult(attempt);
            },
            (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, [("session-1", "session-2")]),
            Ct);

        Assert.Equal(3, made);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        RowEvidence written = JsonSerializer.Deserialize<RowEvidence>(await File.ReadAllTextAsync(Path.Combine(directory, "row3.json"), Ct))!;
        Assert.Equal(ContinuityBounds.MaxAttempts, written.Attempts);
        Assert.True(written.WallSeconds >= 300);
        Assert.Equal(3, written.Steps.Count);
    }

    [Fact]
    public async Task A_rerun_with_its_wall_time_spent_reports_not_proved_without_pulling_the_lever()
    {
        RowEvidence earlier = new("row4", 2, ContinuityBounds.MaxWall.TotalSeconds, Verdict.NotProved, "earlier", [], []);
        await File.WriteAllTextAsync(Path.Combine(directory, "row4.json"), JsonSerializer.Serialize(earlier), Ct);
        bool pulled = false;

        RowResult result = await Run(Runner(true, () => [])).TwoTaskRowAsync("row4", Sent("control"),
            _ => { pulled = true; return Task.CompletedTask; },
            (n, _) => Task.FromResult(new Attempt(n, [])),
            (_, _) => throw new InvalidOperationException("nothing to judge"),
            Ct);

        Assert.False(pulled);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("fresh " + ContinuityEvidence.Variable, result.Message, StringComparison.Ordinal);
        Assert.True(ContinuityEvidence.Left(earlier).Spent);
        Assert.Equal(8, ContinuityEvidence.Left(earlier).Attempts);
    }

    [Fact]
    public async Task A_fresh_directory_is_a_full_budget_and_a_passing_attempt_ends_the_row()
    {
        JsonArray lines = [];
        Step control = Sent("control", route: "health");
        lines.Add(Line(TaskA, control));
        RecordedRunner runner = Runner(true, () => (JsonArray)lines.DeepClone());
        Assert.Equal(RowBudget.Full, await new ContinuityEvidence(directory, runner).BudgetAsync("row3", Ct));

        RowResult result = await Run(runner).TwoTaskRowAsync("row3", control, null,
            (n, _) =>
            {
                Attempt attempt = new(n, [Sent("session-1"), Sent("session-2")]);
                lines.Add(Line(TaskA, attempt.Steps[0]));
                lines.Add(Line(n == 2 ? TaskB : TaskA, attempt.Steps[1]));
                return Task.FromResult(attempt);
            },
            (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, [("session-1", "session-2")]),
            Ct);

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Equal(2, JsonSerializer.Deserialize<RowEvidence>(await File.ReadAllTextAsync(Path.Combine(directory, "row3.json"), Ct))!.Attempts);
    }

    [Fact]
    public async Task A_control_found_on_no_stream_stops_the_row_as_not_proved()
    {
        int made = 0;
        RowResult result = await Run(Runner(true, () => [])).TwoTaskRowAsync("row3", Sent("control", route: "health"), null,
            (n, _) => { made++; return Task.FromResult(new Attempt(n, [Sent("session-1"), Sent("session-2")])); },
            (_, _) => RowResult.Passed("would pass", []),
            Ct);

        Assert.Equal(1, made);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("control", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_evidence_path_git_does_not_ignore_is_refused_before_anything_is_written()
    {
        RecordedRunner runner = Runner(ignored: false, () => []);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(runner).TwoTaskRowAsync("row3", Sent("control"), null, (n, _) => Task.FromResult(new Attempt(n, [])), (_, _) => RowResult.Passed("", []), Ct));

        Assert.Contains("not ignored by git", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory));
        Command check = Assert.Single(runner.Commands);
        Assert.Equal(["-C", directory, "check-ignore", "-q", "--", Path.Combine(directory, "row3.json")], check.Arguments);
    }

    [Fact]
    public async Task No_cookie_or_ticket_reaches_a_step_a_verdict_or_an_evidence_file()
    {
        // The session's cookies, and a response that echoes them back in a Set-Cookie and in its body.
        SessionCookies cookies = new($"__Host-spa-session={Canary}; __Host-spa-csrf={Canary}");
        HttpRequestMessage? seen = null;
        TracedClient client = new((request, _) =>
        {
            seen = request;
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(Canary) };
            response.Headers.Add("Set-Cookie", $"__Host-spa-session={Canary}; Path=/; Secure; HttpOnly");
            return Task.FromResult(response);
        });
        TracedClient failing = new((_, _) => throw new HttpRequestException($"connection to a host with {Canary} reset"));

        Step sent = await client.SendAsync("session-1", HttpMethod.Get, new Uri("https://app1.spa-poc.test:8443/bff/session"), cookies, 200, Ct);
        Step broken = await failing.SendAsync("session-2", HttpMethod.Get, new Uri("https://app1.spa-poc.test:8443/bff/session"), cookies, 200, Ct);

        // The cookie went on the request, and its trace context with it.
        Assert.Equal(cookies.Reveal(), Assert.Single(seen!.Headers.GetValues("Cookie")));
        Assert.Equal($"00-{sent.TraceId}-", Assert.Single(seen.Headers.GetValues(ClientTrace.Header))[..36]);

        JsonArray lines = [Line(TaskA, sent)];
        RowResult result = ContinuityObserver.Crossing([new Attempt(1, [sent, broken])],
            await new ContinuityLog(Cell, Runner(true, () => lines)).ReadAsync(sent.At.AddMinutes(-1), sent.At.AddMinutes(5), Ct), [("session-1", "session-2")]);
        await new ContinuityEvidence(directory, Runner(true, () => [])).WriteAsync("row3", result, [new Attempt(1, [sent, broken])], TimeSpan.FromSeconds(1), Ct);

        string evidence = await File.ReadAllTextAsync(Path.Combine(directory, "row3.json"), Ct);
        Assert.Contains(sent.TraceId, evidence, StringComparison.Ordinal);
        foreach (string printed in new[] { evidence, result.Message, sent.ToString(), broken.ToString(), cookies.ToString(), $"{cookies}", JsonSerializer.Serialize(cookies) })
        {
            Assert.DoesNotContain(Canary, printed, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    public async Task Each_row_test_reports_skipped_naming_the_switch_on_the_local_stack_before_any_aws_call(string? stack)
    {
        ContinuityStack.Environment.Value = name => name == StackSwitch.Variable ? stack : null;
        MethodInfo[] rows = [.. typeof(ContinuityTests).GetMethods().Where(method => method.GetCustomAttribute<FactAttribute>() is not null)];
        Assert.Equal(["Row3", "Row4", "Row5", "Row6", "Row7", "Row8", "Row9"], rows.Select(row => row.Name[..4]).Order(StringComparer.Ordinal));

        foreach (MethodInfo row in rows)
        {
            // Caught by hand: Assert.Throws would hand a skip on to the runner, and this test would report skipped.
            Exception? skipped = null;
            try
            {
                await (Task)row.Invoke(new ContinuityTests(), null)!;
            }
            catch (Exception thrown)
            {
                skipped = thrown;
            }

            Assert.Equal("SkipException", skipped?.GetType().Name);
            Assert.Contains(StackSwitch.Variable, skipped!.Message, StringComparison.Ordinal);
        }
    }

    // ---- row 4's lever (Q145) ----

    private static string Described(params (string Id, string Status)[] tasks) => new JsonObject
    {
        ["tasks"] = new JsonArray([.. tasks.Select(task => (JsonNode)new JsonObject
        {
            ["taskArn"] = $"arn:aws:ecs:us-east-1:123456789012:task/spa-platform-poc/{task.Id}",
            ["lastStatus"] = task.Status,
            ["desiredStatus"] = "RUNNING",
            ["startedAt"] = "2026-10-28T02:00:00.123000+00:00",
            ["containers"] = new JsonArray(new JsonObject { ["name"] = "gateway", ["imageDigest"] = "sha256:aaaa" }),
        })]),
    }.ToJsonString();

    private static RecordedRunner Ecs(params (string Id, string Status)[] tasks) => new(arguments => arguments switch
    {
        ["ecs", "list-tasks", ..] => new JsonObject { ["taskArns"] = new JsonArray([.. tasks.Select(task => (JsonNode)$"arn:aws:ecs:us-east-1:123456789012:task/spa-platform-poc/{task.Id}")]) }.ToJsonString(),
        ["ecs", "describe-tasks", ..] => Described(tasks),
        ["ecs", "stop-task", ..] => "{}",
        _ => throw new InvalidOperationException("unexpected call"),
    });

    [Fact]
    public async Task Row4s_lever_stops_the_task_the_tunnel_does_not_use()
    {
        RecordedRunner runner = Ecs((TaskA, "RUNNING"), (TaskB, "RUNNING"));

        string stopped = await new ContinuityEcs(Cell, runner).StopTheOtherAsync(TaskA, Ct);

        Assert.Equal(TaskB, stopped);
        (_, string[] stop) = Assert.Single(runner.Calls, call => call.Arguments is ["ecs", "stop-task", ..]);
        Assert.Equal(TaskB, stop[Array.IndexOf(stop, "--task") + 1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("c0ffee00c0ffee00c0ffee00c0ffee00")]
    public async Task Row4s_lever_refuses_a_tunnel_task_that_is_not_one_of_the_two_running(string? tunnel)
    {
        RecordedRunner runner = Ecs((TaskA, "RUNNING"), (TaskB, "RUNNING"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ContinuityEcs(Cell, runner).StopTheOtherAsync(tunnel, Ct));
        Assert.DoesNotContain(runner.Calls, call => call.Arguments is ["ecs", "stop-task", ..]);
    }

    [Fact]
    public async Task Row4s_lever_refuses_unless_exactly_two_tasks_run()
    {
        RecordedRunner runner = Ecs((TaskA, "RUNNING"), (TaskB, "PENDING"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ContinuityEcs(Cell, runner).StopTheOtherAsync(TaskA, Ct));
        Assert.DoesNotContain(runner.Calls, call => call.Arguments is ["ecs", "stop-task", ..]);
    }

    [Fact]
    public async Task Row6s_tasks_carry_their_timestamps_and_the_gateway_containers_digest()
    {
        EcsTaskState task = Assert.Single(await new ContinuityEcs(Cell, Ecs((TaskA, "RUNNING"))).ListedAsync(Ct));

        Assert.Equal(new EcsTask(TaskA, new DateTimeOffset(2026, 10, 28, 2, 0, 0, 123, TimeSpan.Zero), null, "sha256:aaaa"), task.Task);
    }
}
