using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The continuity observer (#358) against <see cref="RecordedRunner"/> recordings of the AWS CLI's
/// <c>logs filter-log-events</c>, in the shape the cell's log group gives on <c>main</c> (streams
/// <c>gateway/gateway/&lt;task-id&gt;</c> and <c>mock-api/...</c>, contracts/telemetry.md's lines):
/// for each of #348's rows 3 to 9 a recording that satisfies the row passes, and one recording per
/// rule breaks it and reports failed or not proved, never passed.
/// </summary>
public sealed class ContinuityObserverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Cell Cell => Cell.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")));

    private const string TaskA = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";
    private const string TaskB = "9f8e7d6c5b4a43210fedcba987654321";
    private const string TaskC = "c0ffee00c0ffee00c0ffee00c0ffee00";
    private const string Session = "5e5510a0f00d";

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-28T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A recorded CloudWatch answer: request lines and Refreshed lines on task streams, each with when it was ingested.</summary>
    private sealed class CloudWatch
    {
        private readonly JsonArray events = [];

        public CloudWatch Request(string task, Step step, string? route = null, string? sessionRef = Session, TimeSpan? delivered = null, string stream = "gateway/gateway/") =>
            Line(stream + task, step.At, delivered ?? TimeSpan.FromSeconds(5), new JsonObject
            {
                ["ts"] = step.At.ToString("O"),
                ["level"] = "information",
                ["msg"] = "request",
                ["trace_id"] = step.TraceId,
                ["span_id"] = "00f067aa0ba902b7",
                ["cell"] = "poc",
                ["app_id"] = "app1",
                ["route"] = route ?? step.Route ?? "bff",
                ["rule"] = null,
                ["session_ref"] = sessionRef,
                ["status"] = step.Status ?? 0,
            });

        public CloudWatch Refreshed(string task, DateTimeOffset at, string sessionRef = Session) =>
            Line("gateway/gateway/" + task, at, TimeSpan.FromSeconds(5), new JsonObject
            {
                ["ts"] = at.ToString("O"),
                ["level"] = "information",
                ["msg"] = $"Refreshed the access token for app app1, session {sessionRef}.",
                ["category"] = "Platform.Identity.AccessTokenSource",
                ["app_id"] = "app1",
                ["session_ref"] = sessionRef,
            });

        private CloudWatch Line(string stream, DateTimeOffset at, TimeSpan delivered, JsonObject line)
        {
            events.Add(new JsonObject
            {
                ["logStreamName"] = stream,
                ["timestamp"] = at.ToUnixTimeMilliseconds(),
                ["message"] = line.ToJsonString(),
                ["ingestionTime"] = (at + delivered).ToUnixTimeMilliseconds(),
                ["eventId"] = events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return this;
        }

        public async Task<GatewayCapture> CaptureAsync(DateTimeOffset deadline)
        {
            RecordedRunner runner = new(arguments => arguments is ["logs", "filter-log-events", ..]
                ? new JsonObject { ["events"] = events.DeepClone() }.ToJsonString()
                : throw new InvalidOperationException("the observer asked for more than the log"));
            GatewayCapture capture = await new ContinuityLog(Cell, runner).ReadAsync(T0, deadline, Ct);
            Assert.All(runner.Calls, call => Assert.Contains("gateway/", call.Arguments));
            return capture;
        }
    }

    private static Step Sent(string name, int seconds, int? status = 200, int? expect = 200, string? route = null) =>
        new(name, ClientTrace.Fresh().TraceId, T0.AddSeconds(seconds), status) { Expect = expect, Route = route };

    private static DateTimeOffset Deadline => T0.AddHours(1);

    private static readonly (string, string)[] SessionPair = [("session-1", "session-2")];

    // ---- the control (Q143) ----

    [Fact]
    public async Task The_control_holds_when_its_trace_id_is_in_exactly_one_gateway_stream()
    {
        Step control = Sent("control", 0, route: "health");
        GatewayCapture capture = await new CloudWatch().Request(TaskA, control).CaptureAsync(Deadline);

        Assert.Null(ContinuityObserver.Control(control, capture));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_control_found_in_no_stream_or_in_two_leaves_every_row_not_proved(int streams)
    {
        Step control = Sent("control", 0, route: "health");
        CloudWatch log = new();
        foreach (string task in new[] { TaskA, TaskB }.Take(streams))
        {
            log.Request(task, control);
        }

        RowResult? result = ContinuityObserver.Control(control, await log.CaptureAsync(Deadline));

        Assert.Equal(Verdict.NotProved, Assert.IsType<RowResult>(result).Verdict);
        Assert.StartsWith("not proved:", result.Message, StringComparison.Ordinal);
    }

    // ---- rows 3, 5, 7, 9: the crossing rule (#348 Q19, Q27) ----

    [Fact]
    public async Task Row3_passes_when_an_attempts_two_session_requests_land_on_two_tasks()
    {
        Attempt first = new(1, [Sent("session-1", 0), Sent("session-2", 1)]);
        Attempt second = new(2, [Sent("session-1", 10), Sent("session-2", 11)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, first.Steps[0]).Request(TaskA, first.Steps[1])
            .Request(TaskA, second.Steps[0]).Request(TaskB, second.Steps[1])
            .CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Crossing([first, second], capture, SessionPair);

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains(result.Observations, line => line.Contains(TaskB, StringComparison.Ordinal) && line.EndsWith("crossed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_is_placed_only_by_its_own_trace_id_on_a_gateway_stream()
    {
        // The second request's own line never arrived. Its session's next line is on task B, and the mock
        // API's stream carries its trace id (the gateway forwards the traceparent): neither places it.
        Attempt attempt = new(1, [Sent("session-1", 0), Sent("session-2", 1)]);
        Step later = Sent("session-3", 2);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0])
            .Request(TaskB, later)
            .Request(TaskB, attempt.Steps[1], stream: "mock-api/mock-api/")
            .CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Crossing([attempt], capture, SessionPair);

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Equal(PlacementKind.NoLine, capture.Locate(attempt.Steps[1]).Kind);
    }

    [Fact]
    public async Task A_trace_id_on_two_task_streams_places_the_request_on_neither()
    {
        Attempt attempt = new(1, [Sent("session-1", 0), Sent("session-2", 1)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0])
            .Request(TaskA, attempt.Steps[1]).Request(TaskB, attempt.Steps[1])
            .CaptureAsync(Deadline);

        Assert.Equal(Verdict.NotProved, ContinuityObserver.Crossing([attempt], capture, SessionPair).Verdict);
    }

    [Fact]
    public async Task A_readiness_answer_never_stands_for_a_business_request()
    {
        // The readiness probe landed on B and the business request after it left no line: the probe's task
        // is not the request's (#348 Q19), and a probe named in a pair is never placed at all.
        Step probe = Sent("readyz", 1, route: "health") with { Readiness = true };
        Attempt attempt = new(1, [Sent("session-1", 0), probe, Sent("session-2", 2)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskB, probe)
            .CaptureAsync(Deadline);

        Assert.Equal(Verdict.NotProved, ContinuityObserver.Crossing([attempt], capture, SessionPair).Verdict);
        Assert.Equal(Verdict.NotProved, ContinuityObserver.Crossing([attempt], capture, [("session-1", "readyz")]).Verdict);
        Assert.Equal(PlacementKind.Readiness, capture.Locate(probe).Kind);
    }

    [Fact]
    public async Task A_connection_error_is_recorded_as_missing_coverage_and_never_as_continuity()
    {
        Step broken = Step.Failed("session-2", ClientTrace.Fresh(), T0.AddSeconds(1), new HttpRequestException("refused")) with { Expect = 200 };
        Attempt attempt = new(1, [Sent("session-1", 0), broken]);
        GatewayCapture capture = await new CloudWatch().Request(TaskA, attempt.Steps[0]).CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Crossing([attempt], capture, SessionPair);

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains(result.Observations, line => line.Contains("missing coverage", StringComparison.Ordinal));
        Assert.DoesNotContain("refused", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_that_never_crosses_within_the_bound_is_not_proved()
    {
        List<Attempt> attempts = [];
        CloudWatch log = new();
        for (int n = 1; n <= ContinuityBounds.MaxAttempts; n++)
        {
            Attempt attempt = new(n, [Sent("session-1", n * 10), Sent("session-2", (n * 10) + 1)]);
            log.Request(TaskA, attempt.Steps[0]).Request(TaskA, attempt.Steps[1]);
            attempts.Add(attempt);
        }

        RowResult result = ContinuityObserver.Crossing(attempts, await log.CaptureAsync(Deadline), SessionPair);

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains($"no attempt of {ContinuityBounds.MaxAttempts}", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_challenge_on_any_attempt_fails_the_row()
    {
        Attempt attempt = new(1, [Sent("session-1", 0), Sent("session-2", 1, status: 401)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskB, attempt.Steps[1])
            .CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Crossing([attempt], capture, SessionPair);

        Assert.Equal(Verdict.Failed, result.Verdict);
        Assert.StartsWith("failed:", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_that_arrives_after_the_log_delivery_wait_does_not_count()
    {
        Attempt attempt = new(1, [Sent("session-1", 0), Sent("session-2", 1)]);
        CloudWatch log = new CloudWatch()
            .Request(TaskA, attempt.Steps[0])
            .Request(TaskB, attempt.Steps[1], delivered: ContinuityBounds.LogDelivery + TimeSpan.FromSeconds(1));
        DateTimeOffset waitEnds = attempt.Steps[1].At + ContinuityBounds.LogDelivery;

        Assert.Equal(Verdict.NotProved, ContinuityObserver.Crossing([attempt], await log.CaptureAsync(waitEnds), SessionPair).Verdict);
        // The same line inside the wait crosses: the cut is the wait, not the line.
        Assert.Equal(Verdict.Passed, ContinuityObserver.Crossing([attempt], await log.CaptureAsync(waitEnds.AddSeconds(2)), SessionPair).Verdict);
    }

    [Fact]
    public async Task Row5_passes_once_the_login_and_the_antiforgery_token_have_each_crossed()
    {
        (string, string)[] pairs = [("login-start", "callback"), ("session", "post")];
        Attempt first = new(1, [Sent("login-start", 0, 302, 302, "bff"), Sent("callback", 2, 302, 302, "callback"), Sent("session", 3), Sent("post", 4)]);
        Attempt second = new(2, [Sent("login-start", 10, 302, 302, "bff"), Sent("callback", 12, 302, 302, "callback"), Sent("session", 13), Sent("post", 14)]);
        CloudWatch log = new CloudWatch()
            .Request(TaskA, first.Steps[0]).Request(TaskB, first.Steps[1]).Request(TaskA, first.Steps[2]).Request(TaskA, first.Steps[3])
            // The callback's redirect to the shell carries its trace id on another task: only the callback line places it.
            .Request(TaskA, first.Steps[1], route: "asset");

        RowResult onlyLogin = ContinuityObserver.Crossing([first], await log.CaptureAsync(Deadline), pairs);
        Assert.Equal(Verdict.NotProved, onlyLogin.Verdict);
        Assert.Contains("session and post", onlyLogin.Reason, StringComparison.Ordinal);

        log.Request(TaskA, second.Steps[0]).Request(TaskA, second.Steps[1]).Request(TaskB, second.Steps[2]).Request(TaskA, second.Steps[3]);
        Assert.Equal(Verdict.Passed, ContinuityObserver.Crossing([first, second], await log.CaptureAsync(Deadline), pairs).Verdict);
    }

    [Fact]
    public async Task Row7_passes_when_the_callback_lands_on_the_other_task_and_fails_on_a_wrong_answer()
    {
        (string, string)[] pairs = [("login-start", "callback")];
        Attempt attempt = new(1, [Sent("login-start", 0, 302, 302, "bff"), Sent("callback", 3, 302, 302, "callback")]);
        GatewayCapture capture = await new CloudWatch().Request(TaskA, attempt.Steps[0]).Request(TaskB, attempt.Steps[1]).CaptureAsync(Deadline);

        Assert.Equal(Verdict.Passed, ContinuityObserver.Crossing([attempt], capture, pairs).Verdict);

        Attempt refused = new(1, [attempt.Steps[0], attempt.Steps[1] with { Status = 404 }]);
        Assert.Equal(Verdict.Failed, ContinuityObserver.Crossing([refused], capture, pairs).Verdict);
    }

    private static readonly (string, string)[] LogoutPair = [("logout", "replay")];

    private static Attempt Row9Attempt(int seconds, int controlStatus = 200, int replayStatus = 401) => new(1,
    [
        Sent("control", seconds, controlStatus, expect: null),
        Sent("logout", seconds + 1, 302, 302),
        Sent("replay", seconds + 2, replayStatus, 401, "bff"),
    ]);

    [Fact]
    public async Task Row9_passes_when_the_replay_after_logout_is_refused_by_the_other_task()
    {
        Attempt attempt = Row9Attempt(0);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskA, attempt.Steps[1]).Request(TaskB, attempt.Steps[2])
            .CaptureAsync(Deadline);

        Assert.Equal(Verdict.Passed, ContinuityObserver.Crossing([attempt], capture, LogoutPair, ["control"]).Verdict);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 401)]
    [InlineData(true, 0)]
    public async Task Row9s_pre_logout_replay_without_its_200_control_proves_nothing(bool controlSent, int controlStatus)
    {
        Attempt full = Row9Attempt(0, controlStatus);
        Attempt attempt = controlSent
            ? controlStatus == 0 ? full with { Steps = [Step.Failed("control", ClientTrace.Fresh(), T0, new IOException()), .. full.Steps.Skip(1)] } : full
            : full with { Steps = [.. full.Steps.Skip(1)] };
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, full.Steps[0]).Request(TaskA, full.Steps[1]).Request(TaskB, full.Steps[2])
            .CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Crossing([attempt], capture, LogoutPair, ["control"]);

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains(result.Observations, line => line.Contains("no 200 control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Row9_fails_when_the_other_task_still_serves_the_ended_session()
    {
        Attempt attempt = Row9Attempt(0, replayStatus: 200);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskA, attempt.Steps[1]).Request(TaskB, attempt.Steps[2])
            .CaptureAsync(Deadline);

        Assert.Equal(Verdict.Failed, ContinuityObserver.Crossing([attempt], capture, LogoutPair, ["control"]).Verdict);
    }

    // ---- row 4: graceful replacement (#348 Q20, Q21) ----

    [Fact]
    public async Task Row4_passes_when_the_session_answers_on_the_survivor_and_then_on_the_replacement()
    {
        Step gap = Step.Failed("call-2", ClientTrace.Fresh(), T0.AddSeconds(2), new HttpRequestException()) with { Expect = 200 };
        Attempt attempt = new(1, [Sent("call-1", 1), gap, Sent("call-3", 30), Sent("call-4", 60)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskA, attempt.Steps[2]).Request(TaskC, attempt.Steps[3])
            .CaptureAsync(Deadline);

        RowResult result = ContinuityObserver.Replacement(TaskA, TaskB, [attempt], capture);

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains(result.Observations, line => line.Contains("missing coverage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Row4_is_not_proved_when_no_request_reaches_a_replacement_and_failed_on_a_challenge()
    {
        Attempt attempt = new(1, [Sent("call-1", 1), Sent("call-2", 30), Sent("call-3", 31)]);
        GatewayCapture capture = await new CloudWatch()
            .Request(TaskA, attempt.Steps[0]).Request(TaskA, attempt.Steps[1]).Request(TaskB, attempt.Steps[2])
            .CaptureAsync(Deadline);

        // The stopped task draining is not a replacement.
        Assert.Equal(Verdict.NotProved, ContinuityObserver.Replacement(TaskA, TaskB, [attempt], capture).Verdict);

        Attempt challenged = new(1, [attempt.Steps[0], attempt.Steps[1] with { Status = 401 }]);
        Assert.Equal(Verdict.Failed, ContinuityObserver.Replacement(TaskA, TaskB, [challenged], capture).Verdict);
    }

    // ---- row 6: rolling deployment (Q146, Q149, Q150) ----

    private static readonly EcsTask[] OldTasks =
    [
        new(TaskA, T0.AddMinutes(-60), T0.AddMinutes(6), "sha256:aaaa"),
        new(TaskB, T0.AddMinutes(-60), T0.AddMinutes(7), "sha256:aaaa"),
    ];

    private static readonly EcsTask[] NewTasks =
    [
        new(TaskC, T0.AddMinutes(2), null, "sha256:bbbb"),
        new("d00dd00dd00dd00dd00dd00dd00dd00d", T0.AddMinutes(3), null, "sha256:bbbb"),
    ];

    private static (ContinuityObserver.RollingDeploy Deploy, CloudWatch Log) Rolling(int refreshMinute = 4, int refreshes = 1, int status = 200, string callbackTask = TaskC)
    {
        Step[] calls = [.. Enumerable.Range(0, 5).Select(i => Sent($"call-{i}", 60 + (i * 60), status, expect: null))];
        Step start = Sent("login-start", 150, 302, route: "bff");
        Step callback = Sent("callback", 480, 302, 302, "callback");
        CloudWatch log = new();
        foreach (Step call in calls)
        {
            log.Request(call.At < T0.AddMinutes(5) ? TaskA : TaskC, call);
        }

        log.Request(TaskB, start).Request(callbackTask, callback);
        for (int r = 0; r < refreshes; r++)
        {
            log.Refreshed(TaskA, T0.AddMinutes(refreshMinute).AddSeconds(r));
        }

        return (new ContinuityObserver.RollingDeploy(OldTasks, NewTasks, [calls], start, callback), log);
    }

    [Fact]
    public async Task Row6_passes_with_one_refresh_inside_the_interval_and_a_login_straddling_the_drain()
    {
        (ContinuityObserver.RollingDeploy deploy, CloudWatch log) = Rolling();

        RowResult result = ContinuityObserver.Rolling(deploy, await log.CaptureAsync(Deadline));

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains(result.Observations, line => line.Contains("sha256:aaaa", StringComparison.Ordinal) && line.Contains("sha256:bbbb", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Row6_a_refresh_outside_the_interval_is_not_proved(int minute)
    {
        // The interval runs from the first new task's start (minute 2) to the last old task's stop (minute 7).
        (ContinuityObserver.RollingDeploy deploy, CloudWatch log) = Rolling(refreshMinute: minute);

        Assert.Equal(Verdict.NotProved, ContinuityObserver.Rolling(deploy, await log.CaptureAsync(Deadline)).Verdict);
    }

    [Fact]
    public async Task Row6_two_refreshed_lines_for_one_session_fail_it()
    {
        (ContinuityObserver.RollingDeploy deploy, CloudWatch log) = Rolling(refreshes: 2);

        Assert.Equal(Verdict.Failed, ContinuityObserver.Rolling(deploy, await log.CaptureAsync(Deadline)).Verdict);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(502)]
    public async Task Row6_a_401_or_a_5xx_on_any_call_fails_it(int status)
    {
        (ContinuityObserver.RollingDeploy deploy, CloudWatch log) = Rolling(status: status);

        Assert.Equal(Verdict.Failed, ContinuityObserver.Rolling(deploy, await log.CaptureAsync(Deadline)).Verdict);
    }

    [Fact]
    public async Task Row6_a_callback_on_an_old_task_or_an_interval_still_open_is_not_proved()
    {
        (ContinuityObserver.RollingDeploy deploy, CloudWatch log) = Rolling(callbackTask: TaskB);
        Assert.Equal(Verdict.NotProved, ContinuityObserver.Rolling(deploy, await log.CaptureAsync(Deadline)).Verdict);

        (deploy, log) = Rolling();
        ContinuityObserver.RollingDeploy open = deploy with { OldTasks = [OldTasks[0], OldTasks[1] with { StoppedAt = null }] };
        Assert.Equal(Verdict.NotProved, ContinuityObserver.Rolling(open, await log.CaptureAsync(Deadline)).Verdict);
    }

    // ---- row 8: fifty calls (Q147) ----

    private static (Step[] Calls, CloudWatch Log) Fifty(Func<int, string>? task = null, int refreshes = 1, int status = 200, TimeSpan? lastDelivered = null)
    {
        Step[] calls = [.. Enumerable.Range(0, ContinuityBounds.Row8Calls).Select(i => Sent($"call-{i}", 1, status))];
        CloudWatch log = new();
        for (int i = 0; i < calls.Length; i++)
        {
            log.Request((task ?? (n => n % 2 == 0 ? TaskA : TaskB))(i), calls[i], delivered: i == calls.Length - 1 ? lastDelivered : null);
        }

        for (int r = 0; r < refreshes; r++)
        {
            log.Refreshed(r % 2 == 0 ? TaskA : TaskB, T0.AddSeconds(1));
        }

        return (calls, log);
    }

    private static DateTimeOffset Row8Deadline => T0.AddSeconds(1) + ContinuityBounds.LogDelivery;

    [Fact]
    public async Task Row8_passes_with_fifty_200s_from_both_streams_and_exactly_one_refreshed_line()
    {
        (Step[] calls, CloudWatch log) = Fifty();

        Assert.Equal(Verdict.Passed, ContinuityObserver.FiftyCalls(calls, await log.CaptureAsync(Row8Deadline)).Verdict);
    }

    [Theory]
    [InlineData(0, "no Refreshed line")]
    [InlineData(2, "2 Refreshed lines")]
    public async Task Row8_zero_or_two_refreshed_lines_on_a_complete_capture_fail_it(int refreshes, string reason)
    {
        (Step[] calls, CloudWatch log) = Fifty(refreshes: refreshes);

        RowResult result = ContinuityObserver.FiftyCalls(calls, await log.CaptureAsync(Row8Deadline));
        Assert.Equal(Verdict.Failed, result.Verdict);
        Assert.StartsWith(reason, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row8_any_non_200_fails_it()
    {
        (Step[] calls, CloudWatch log) = Fifty(status: 503);

        Assert.Equal(Verdict.Failed, ContinuityObserver.FiftyCalls(calls, await log.CaptureAsync(Row8Deadline)).Verdict);
    }

    [Fact]
    public async Task Row8_one_task_serving_every_call_or_a_line_after_the_wait_is_not_proved()
    {
        (Step[] calls, CloudWatch log) = Fifty(task: _ => TaskA);
        Assert.Equal(Verdict.NotProved, ContinuityObserver.FiftyCalls(calls, await log.CaptureAsync(Row8Deadline)).Verdict);

        (calls, log) = Fifty(refreshes: 0, lastDelivered: ContinuityBounds.LogDelivery + TimeSpan.FromSeconds(1));
        RowResult late = ContinuityObserver.FiftyCalls(calls, await log.CaptureAsync(Row8Deadline));
        // Zero Refreshed lines fail only a complete capture; here a call's line never arrived.
        Assert.Equal(Verdict.NotProved, late.Verdict);
        Assert.Contains("incomplete", late.Reason, StringComparison.Ordinal);
    }
}
