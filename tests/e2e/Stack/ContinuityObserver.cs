using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The constants of the deployed continuity rows (#358, Q144): every two-task row (3, 4, 5, 7, 9) has
/// at most <see cref="MaxAttempts"/> attempts and <see cref="MaxWall"/> of wall time, reruns included,
/// and each attempt, and row 8, waits <see cref="LogDelivery"/> for its lines to reach CloudWatch.
/// </summary>
public static class ContinuityBounds
{
    public const int MaxAttempts = 10;

    public static readonly TimeSpan MaxWall = TimeSpan.FromMinutes(15);

    /// <summary>How long after its last request an attempt waits before it reads the log; a line ingested later never counts (Q144).</summary>
    public static readonly TimeSpan LogDelivery = TimeSpan.FromSeconds(90);

    /// <summary>Row 6: a new session is signed in this often from the row's start until the interval ends (Q149).</summary>
    public static readonly TimeSpan Row6SignInEvery = TimeSpan.FromSeconds(60);

    /// <summary>Row 6: each session calls once a second for this long, then stops (Q149).</summary>
    public static readonly TimeSpan Row6SessionCalls = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Row 6: the straddling login's callback must come within the handler's default remote
    /// authentication timeout, 15 minutes (Q150;
    /// https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.remoteauthenticationoptions.remoteauthenticationtimeout).
    /// </summary>
    public static readonly TimeSpan RemoteAuthenticationTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Row 8: the parallel calls on one due token (#348 row 8).</summary>
    public const int Row8Calls = 50;
}

/// <summary>
/// A fresh, sampled W3C trace context for one request the driver sends (#358 Q137): the request is
/// found later by the <c>trace_id</c> of its own request line, which the gateway takes from this
/// header (pinned by the composed test <c>InboundTraceContextTests</c>), and by no other field.
/// Version 00, flags 01 (https://www.w3.org/TR/trace-context/#traceparent-header).
/// </summary>
public sealed record ClientTrace(string TraceId, string ParentId)
{
    public const string Header = "traceparent";

    public string Traceparent => $"00-{TraceId}-{ParentId}-01";

    public static ClientTrace Fresh() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
}

/// <summary>
/// One request the driver sent, as the observer and the evidence file hold it: a name the row gives
/// it, its client trace id, when it was sent, and what came back. Nothing in it is a cookie, a token,
/// a session key or a ticket, so it can be written to an evidence file and printed (N-16). A
/// connection error keeps its exception's type alone: a message can carry a URL.
/// </summary>
public sealed record Step(string Name, string TraceId, DateTimeOffset At, int? Status)
{
    /// <summary>The status the row requires; any other answer fails the row. Null for a step the row only places.</summary>
    public int? Expect { get; init; }

    /// <summary>The type of the exception that stopped the request before any answer: missing coverage, never continuity (#348 Q20).</summary>
    public string? ConnectionError { get; init; }

    /// <summary>A readiness probe: never located, so it never stands for a business request (#348 Q19).</summary>
    public bool Readiness { get; init; }

    /// <summary>
    /// The request line's <c>route</c>, for a browser step whose trace id rides on every request of a
    /// navigation (Q137: the context's extra HTTP headers): a callback's redirect to the shell carries
    /// the callback's trace id too, and only the <c>callback</c> line is the callback's.
    /// </summary>
    public string? Route { get; init; }

    public static Step Failed(string name, ClientTrace trace, DateTimeOffset at, Exception error) =>
        new(name, trace.TraceId, at, null) { ConnectionError = error.GetType().Name };
}

/// <summary>One attempt of a two-task row: its steps in the order sent.</summary>
public sealed record Attempt(int Number, IReadOnlyList<Step> Steps);

/// <summary>A gateway task as ECS describes it: its id, when it started and stopped, and its image digest.</summary>
public sealed record EcsTask(string Id, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, string? Digest);

/// <summary>Passed, failed, or not proved (Q138). "Inconclusive" is not proved; missing coverage is an observation, never a verdict.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    Passed,
    Failed,
    NotProved,
}

/// <summary>A row's verdict, the reason for it, and what was observed on the way.</summary>
public sealed record RowResult(Verdict Verdict, string Reason, IReadOnlyList<string> Observations)
{
    /// <summary>The message a row test reports: <c>failed:</c> or <c>not proved:</c> for anything but a pass (Q138).</summary>
    public string Message => (Verdict switch
    {
        Verdict.Passed => "passed: ",
        Verdict.Failed => "failed: ",
        _ => "not proved: ",
    }) + Reason + (Observations.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Observations));

    /// <summary>Passes the row test only on a pass; a not-proved row is a red test (Q138).</summary>
    public void Assert()
    {
        if (Verdict != Verdict.Passed)
        {
            Xunit.Assert.Fail(Message);
        }
    }

    public static RowResult Passed(string reason, IReadOnlyList<string> observations) => new(Verdict.Passed, reason, observations);

    public static RowResult Failed(string reason, IReadOnlyList<string> observations) => new(Verdict.Failed, reason, observations);

    public static RowResult NotProved(string reason, IReadOnlyList<string> observations) => new(Verdict.NotProved, reason, observations);
}

/// <summary>One CloudWatch event of a gateway stream: the stream, its task id, and the JSON line.</summary>
public sealed record GatewayEvent(string Stream, string TaskId, DateTimeOffset Timestamp, DateTimeOffset Ingested, JsonElement Line)
{
    public string? Field(string name) =>
        Line.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public bool IsRequest => Field("msg") == "request";

    /// <summary>
    /// The identity library's Information line for a refresh (src/Platform.Identity/AccessTokenSource.cs,
    /// "Refreshed the access token for app {AppId}, session {SessionRef}."), written with
    /// <c>session_ref</c> as a field.
    /// </summary>
    public bool IsRefreshed => Field("msg")?.StartsWith("Refreshed the access token", StringComparison.Ordinal) == true;

    /// <summary>The line's own <c>ts</c>, which row 6 compares with the interval (Q146).</summary>
    public DateTimeOffset? Ts =>
        Field("ts") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at) ? at : null;
}

/// <summary>Where a step was found: on one task, nowhere, on several streams, or not looked for.</summary>
public enum PlacementKind
{
    OnTask,
    NoLine,
    SeveralStreams,
    ConnectionError,
    Readiness,
}

public sealed record Placement(PlacementKind Kind, string? TaskId)
{
    public override string ToString() => Kind switch
    {
        PlacementKind.OnTask => $"on task {TaskId}",
        PlacementKind.NoLine => "no request line with its trace id",
        PlacementKind.SeveralStreams => "request lines with its trace id on several streams",
        PlacementKind.ConnectionError => "a connection error (missing coverage)",
        _ => "a readiness probe, which stands for no business request",
    };
}

/// <summary>
/// The gateway's lines as one read of CloudWatch found them, cut at a delivery deadline: a line
/// ingested after it never arrived as far as the row is concerned (Q144, Q147).
/// </summary>
public sealed class GatewayCapture(IReadOnlyList<GatewayEvent> events)
{
    public IReadOnlyList<GatewayEvent> Events { get; } = events;

    /// <summary>
    /// The task that served the step: the one gateway stream holding a request line whose
    /// <c>trace_id</c> is the step's client trace id (Q137). No other field places a request: not the
    /// session, not the time, not the request before it.
    /// </summary>
    public Placement Locate(Step step)
    {
        if (step.Readiness)
        {
            return new Placement(PlacementKind.Readiness, null);
        }

        if (step.ConnectionError is not null)
        {
            return new Placement(PlacementKind.ConnectionError, null);
        }

        string[] tasks = [.. RequestLines(step).Select(line => line.TaskId).Distinct(StringComparer.Ordinal)];
        return tasks.Length switch
        {
            0 => new Placement(PlacementKind.NoLine, null),
            1 => new Placement(PlacementKind.OnTask, tasks[0]),
            _ => new Placement(PlacementKind.SeveralStreams, null),
        };
    }

    /// <summary>The <c>session_ref</c> the step's request line carries, if it was found on one task.</summary>
    public string? SessionRefOf(Step step) =>
        Locate(step).Kind == PlacementKind.OnTask ? RequestLines(step).Select(line => line.Field("session_ref")).FirstOrDefault(value => value is not null) : null;

    /// <summary>Every Refreshed line for the session, across every gateway stream.</summary>
    public IReadOnlyList<GatewayEvent> Refreshed(string sessionRef) =>
        [.. Events.Where(line => line.IsRefreshed && line.Field("session_ref") == sessionRef)];

    private IEnumerable<GatewayEvent> RequestLines(Step step) =>
        Events.Where(line => line.IsRequest && line.Field("trace_id") == step.TraceId && (step.Route is null || line.Field("route") == step.Route));
}

/// <summary>
/// Reads the gateway's lines with their streams, through the AWS CLI (<see cref="ICommandRunner"/>),
/// the way <see cref="CellAws.GatewayLinesAsync"/> does, keeping what that drops: the stream, so the
/// task (the awslogs driver names a stream <c>prefix/container/task-id</c>,
/// https://docs.aws.amazon.com/AmazonECS/latest/developerguide/using_awslogs.html), and the ingestion
/// time, so a line that arrived after the wait is left out.
/// </summary>
public sealed class ContinuityLog(Cell cell, ICommandRunner runner)
{
    public async Task<GatewayCapture> ReadAsync(DateTimeOffset since, DateTimeOffset deadline, CancellationToken ct)
    {
        string output = await runner.RunAsync("aws",
            ["logs", "filter-log-events", "--log-group-name", cell.LogGroup, "--log-stream-name-prefix", CellAws.GatewayStreamPrefix,
             "--start-time", since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), "--output", "json", "--region", cell.Region], ct);

        using JsonDocument answer = JsonDocument.Parse(output);
        List<GatewayEvent> events = [];
        foreach (JsonElement logEvent in answer.RootElement.GetProperty("events").EnumerateArray())
        {
            string stream = logEvent.GetProperty("logStreamName").GetString() ?? "";
            DateTimeOffset ingested = DateTimeOffset.FromUnixTimeMilliseconds(logEvent.GetProperty("ingestionTime").GetInt64());
            if (!stream.StartsWith(CellAws.GatewayStreamPrefix, StringComparison.Ordinal)
                || ingested > deadline
                || logEvent.GetProperty("message").GetString() is not { } message
                || !message.TrimStart().StartsWith('{'))
            {
                continue;
            }

            try
            {
                using JsonDocument line = JsonDocument.Parse(message);
                events.Add(new GatewayEvent(stream, stream[(stream.LastIndexOf('/') + 1)..],
                    DateTimeOffset.FromUnixTimeMilliseconds(logEvent.GetProperty("timestamp").GetInt64()), ingested, line.RootElement.Clone()));
            }
            catch (JsonException)
            {
                // A line that opens with a brace and is not JSON is not a contract line.
            }
        }

        return new GatewayCapture([.. events.OrderBy(entry => entry.Timestamp)]);
    }
}

/// <summary>
/// The observer (#358 Q136): a row's requests and the AWS CLI's answers in, a verdict out. It sends
/// nothing and pulls no lever, so every rule here is proved against recordings
/// (<c>tests/e2e/Deployed/ContinuityObserverTests.cs</c>), one rule at a time.
/// </summary>
public static class ContinuityObserver
{
    /// <summary>
    /// The control the class runs first on the two-task stack (Q143): one raw request whose client
    /// trace id is found in exactly one gateway stream. Null when it holds; otherwise the not-proved
    /// verdict every row then reports.
    /// </summary>
    public static RowResult? Control(Step control, GatewayCapture capture)
    {
        Placement placed = capture.Locate(control);
        return placed.Kind == PlacementKind.OnTask && control.ConnectionError is null
            ? null
            : RowResult.NotProved($"the control request {control.TraceId} was {placed}, so no request can be placed by its trace id", []);
    }

    /// <summary>
    /// Rows 3, 5, 7 and 9 (#348 Q27): every step answers what it must, and each named pair of steps
    /// has been served by two different tasks in some attempt. A connection error is missing
    /// coverage. A pair whose steps were not both placed, or landed on one task, does not cross; none
    /// crossing within the attempts made is not proved, never a pass. An attempt whose
    /// <paramref name="controls"/> did not each answer 200 proves nothing (row 9's pre-logout replay, Q28).
    /// </summary>
    public static RowResult Crossing(IReadOnlyList<Attempt> attempts, GatewayCapture capture, IReadOnlyList<(string First, string Second)> pairs, IReadOnlyList<string>? controls = null)
    {
        List<string> observed = [];
        HashSet<int> crossed = [];
        foreach (Attempt attempt in attempts)
        {
            if (WrongAnswer(attempt, observed) is { } failed)
            {
                return failed;
            }

            List<string> missingControls = [.. (controls ?? []).Where(name =>
                attempt.Steps.FirstOrDefault(step => step.Name == name) is not { ConnectionError: null, Status: 200 })];
            if (missingControls.Count > 0)
            {
                observed.Add($"attempt {attempt.Number}: no 200 control from {string.Join(", ", missingControls)}, so it proves nothing");
                continue;
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                (string first, string second) = pairs[i];
                Placement a = Place(attempt, first, capture);
                Placement b = Place(attempt, second, capture);
                bool crosses = a.Kind == PlacementKind.OnTask && b.Kind == PlacementKind.OnTask && a.TaskId != b.TaskId;
                observed.Add($"attempt {attempt.Number}: {first} {a}, {second} {b}{(crosses ? ": crossed" : "")}");
                if (crosses)
                {
                    crossed.Add(i);
                }
            }
        }

        if (crossed.Count == pairs.Count)
        {
            return RowResult.Passed($"each pair crossed tasks within {attempts.Count} attempts", observed);
        }

        string missing = string.Join(", ", pairs.Where((_, i) => !crossed.Contains(i)).Select(pair => $"{pair.First} and {pair.Second}"));
        return RowResult.NotProved($"no attempt of {attempts.Count} placed {missing} on two different tasks", observed);
    }

    /// <summary>
    /// Row 4 (#348 Q20, Q21): after <c>aws ecs stop-task</c> on the task the tunnel does not use, the
    /// session answers 200 on the survivor, and a later request of it is served by a task that was not
    /// running at the row's start: the replacement.
    /// </summary>
    public static RowResult Replacement(string survivor, string stopped, IReadOnlyList<Attempt> attempts, GatewayCapture capture)
    {
        List<string> observed = [$"stopped {stopped}; the tunnel's hop {survivor} survives"];
        DateTimeOffset? onSurvivor = null;
        foreach (Attempt attempt in attempts)
        {
            if (WrongAnswer(attempt, observed) is { } failed)
            {
                return failed;
            }

            foreach (Step step in attempt.Steps)
            {
                Placement placed = capture.Locate(step);
                observed.Add($"attempt {attempt.Number}: {step.Name} {placed}");
                if (placed.Kind != PlacementKind.OnTask || step.Status != 200)
                {
                    continue;
                }

                if (placed.TaskId == survivor)
                {
                    onSurvivor ??= step.At;
                }
                else if (placed.TaskId != stopped && onSurvivor is { } before && step.At > before)
                {
                    return RowResult.Passed($"the session answered on {survivor} after the stop and then on the replacement {placed.TaskId}", observed);
                }
            }
        }

        return RowResult.NotProved(onSurvivor is null
            ? $"no request after the stop was placed on the survivor {survivor}"
            : "no request was placed on a replacement task", observed);
    }

    /// <summary>What row 6 recorded (Q146, Q149, Q150).</summary>
    public sealed record RollingDeploy(
        IReadOnlyList<EcsTask> OldTasks,
        IReadOnlyList<EcsTask> NewTasks,
        IReadOnlyList<IReadOnlyList<Step>> Sessions,
        Step? LoginStart,
        Step? LoginCallback);

    /// <summary>
    /// Row 6 (Q146, Q149, Q150). The interval runs from the earliest <c>startedAt</c> of a new task to
    /// the latest <c>stoppedAt</c> of the old ones. Failed: an observed call answered 401 or 5xx, or a
    /// session has two or more Refreshed lines. Not proved: no session's single Refreshed line falls in
    /// the interval, or the straddling login's start is not on an old task, its callback not on a new
    /// one, or not within the remote authentication timeout.
    /// </summary>
    public static RowResult Rolling(RollingDeploy deploy, GatewayCapture capture)
    {
        List<string> observed = [];
        observed.Add($"old tasks {Describe(deploy.OldTasks)}; new tasks {Describe(deploy.NewTasks)}");

        HashSet<string> sessionRefs = [];
        for (int s = 0; s < deploy.Sessions.Count; s++)
        {
            foreach (Step call in deploy.Sessions[s])
            {
                if (call.ConnectionError is not null)
                {
                    observed.Add($"session {s + 1}: {call.Name}: a connection error, missing coverage");
                }
                else if (call.Status is { } status && (status >= 500 || status == Contract.Error("unauthenticated").Status))
                {
                    return RowResult.Failed($"session {s + 1}: {call.Name} answered {call.Status}", observed);
                }
            }

            string? sessionRef = deploy.Sessions[s].Select(capture.SessionRefOf).FirstOrDefault(value => value is not null);
            if (sessionRef is not null)
            {
                sessionRefs.Add(sessionRef);
            }
        }

        foreach (string sessionRef in sessionRefs)
        {
            if (capture.Refreshed(sessionRef).Count >= 2)
            {
                return RowResult.Failed($"session {sessionRef} has {capture.Refreshed(sessionRef).Count} Refreshed lines", observed);
            }
        }

        if (deploy.NewTasks.Count == 0 || deploy.NewTasks.Any(task => task.StartedAt is null)
            || deploy.OldTasks.Count == 0 || deploy.OldTasks.Any(task => task.StoppedAt is null))
        {
            return RowResult.NotProved("the replacement interval is not known: a new task with no startedAt or an old one with no stoppedAt", observed);
        }

        DateTimeOffset from = deploy.NewTasks.Min(task => task.StartedAt!.Value);
        DateTimeOffset to = deploy.OldTasks.Max(task => task.StoppedAt!.Value);
        observed.Add($"interval {from:O} to {to:O}");

        if (Straddling(deploy, capture, observed) is { } login)
        {
            return login;
        }

        string? inside = sessionRefs.FirstOrDefault(sessionRef =>
            capture.Refreshed(sessionRef) is [{ Ts: { } ts }] && ts >= from && ts <= to);
        return inside is null
            ? RowResult.NotProved("no Refreshed line inside the interval", observed)
            : RowResult.Passed($"session {inside} refreshed once, inside the interval, and no call was refused as unauthenticated or session_ended or got a 5xx", observed);
    }

    private static RowResult? Straddling(RollingDeploy deploy, GatewayCapture capture, List<string> observed)
    {
        if (deploy.LoginStart is not { } start || deploy.LoginCallback is not { } callback)
        {
            return RowResult.NotProved("the straddling login did not reach its callback", observed);
        }

        Placement first = capture.Locate(start);
        Placement last = capture.Locate(callback);
        observed.Add($"straddling login: start {first}, callback {last}");
        if (first.Kind != PlacementKind.OnTask || !deploy.OldTasks.Any(task => task.Id == first.TaskId))
        {
            return RowResult.NotProved("the straddling login's start is not on an old task", observed);
        }

        if (last.Kind != PlacementKind.OnTask || !deploy.NewTasks.Any(task => task.Id == last.TaskId))
        {
            return RowResult.NotProved("the straddling login's callback is not on a new task", observed);
        }

        if (callback.At - start.At > ContinuityBounds.RemoteAuthenticationTimeout)
        {
            return RowResult.NotProved("the straddling login's callback came after the remote authentication timeout", observed);
        }

        return callback.Expect is { } expect && callback.Status != expect
            ? RowResult.Failed($"the straddling login's callback answered {callback.Status}, not {expect}", observed)
            : null;
    }

    /// <summary>
    /// Row 8 (Q147): every call answers 200, the calls' lines come from both task streams, and exactly
    /// one Refreshed line for the session appears across every gateway stream. Any non-200 answer or a
    /// count other than one on a complete capture fails it; one task serving every call, or a call
    /// whose line never arrived within the wait, leaves it not proved. It claims no grant count.
    /// </summary>
    public static RowResult FiftyCalls(IReadOnlyList<Step> calls, GatewayCapture capture)
    {
        List<string> observed = [];
        if (calls.FirstOrDefault(call => call.ConnectionError is null && call.Status != 200) is { } wrong)
        {
            return RowResult.Failed($"{wrong.Name} answered {wrong.Status}", observed);
        }

        Placement[] placed = [.. calls.Select(capture.Locate)];
        string[] sessionRefs = [.. calls.Select(capture.SessionRefOf).OfType<string>().Distinct(StringComparer.Ordinal)];
        int refreshed = sessionRefs.Length == 1 ? capture.Refreshed(sessionRefs[0]).Count : 0;
        observed.Add($"{calls.Count(call => call.Status == 200)} of {calls.Count} calls answered 200; {Tally(placed)}; session {string.Join(",", sessionRefs)} with {refreshed} Refreshed lines");

        if (refreshed >= 2)
        {
            return RowResult.Failed($"{refreshed} Refreshed lines for one session", observed);
        }

        if (placed.Any(place => place.Kind != PlacementKind.OnTask) || sessionRefs.Length != 1)
        {
            return RowResult.NotProved("the capture is incomplete: a call's line did not arrive within the wait, or no call was placed on one task", observed);
        }

        if (placed.Select(place => place.TaskId).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return RowResult.NotProved("one task served every call", observed);
        }

        return refreshed == 1
            ? RowResult.Passed("every call answered 200 across both tasks, with one Refreshed line", observed)
            : RowResult.Failed("no Refreshed line for the session on a complete capture", observed);
    }

    private static RowResult? WrongAnswer(Attempt attempt, List<string> observed)
    {
        foreach (Step step in attempt.Steps)
        {
            if (step.ConnectionError is not null)
            {
                observed.Add($"attempt {attempt.Number}: {step.Name}: a connection error ({step.ConnectionError}), missing coverage");
            }
            else if (step.Expect is { } expect && step.Status != expect)
            {
                return RowResult.Failed($"attempt {attempt.Number}: {step.Name} answered {step.Status}, not {expect}", observed);
            }
        }

        return null;
    }

    private static Placement Place(Attempt attempt, string name, GatewayCapture capture) =>
        attempt.Steps.FirstOrDefault(step => step.Name == name) is { } step ? capture.Locate(step) : new Placement(PlacementKind.NoLine, null);

    private static string Describe(IReadOnlyList<EcsTask> tasks) =>
        tasks.Count == 0 ? "none" : string.Join(", ", tasks.Select(task => $"{task.Id} ({task.Digest ?? "no digest"})"));

    private static string Tally(IEnumerable<Placement> placed) =>
        string.Join(", ", placed.GroupBy(place => place.ToString()).Select(group => $"{group.Count()} {group.Key}"));
}
