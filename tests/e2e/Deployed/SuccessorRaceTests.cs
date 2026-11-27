using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// O3's drive (<see cref="SuccessorRace"/>, #1529) with no AWS and no browser: the sessions answered by
/// a double that holds the refresh open until the logout is sent, and the drive's waits recorded rather
/// than slept. Each test names in its body the mutation that turns it red.
/// </summary>
public sealed class SuccessorRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LiveFragment Orders = new("orders", "f1", "entry.js", ["orders"]);

    /// <summary>The sessions: one sign-in, calls answered 200, and the refresh held until the logout has been sent.</summary>
    private sealed class Sessions : ISuccessorSessions
    {
        private readonly TaskCompletionSource loggedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Sent { get; } = [];

        public List<string> Paths { get; } = [];

        public bool SignedIn { get; init; } = true;

        public Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct)
        {
            Sent.Add(name);
            Step step = new(name, ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 302) { Route = "bff" };
            return Task.FromResult<(Step, SessionCookies?)>((step, SignedIn ? new SessionCookies("s01") : null));
        }

        public async Task<Step> SendAsync(string name, string path, SessionCookies cookies, CancellationToken ct)
        {
            Sent.Add(name);
            Paths.Add(path);
            Step step = new(name, ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 200) { Route = "api" };
            if (name == "refresh")
            {
                await loggedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }

            return step;
        }

        public Task<Step> LogoutAsync(string name, SessionCookies cookies, CancellationToken ct)
        {
            Sent.Add(name);
            loggedOut.TrySetResult();
            return Task.FromResult(new Step(name, ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 302) { Route = "bff" });
        }

        public Task<IReadOnlyList<Step>> OverlapAsync(string name, string fallbackFragment, CancellationToken ct) =>
            throw new InvalidOperationException("O3 overlaps no callback");
    }

    [Fact]
    public async Task The_logout_is_sent_while_the_fragments_refresh_is_in_flight_once_its_set_is_due()
    {
        // Mutation: awaiting the refresh before the logout is sent in SuccessorRace.RunAsync leaves the double's refresh
        // waiting on a logout that never comes, and this fails. Mutation: dropping the DueAfter wait fails the waits.
        Sessions sessions = new();
        List<TimeSpan> waits = [];
        SuccessorRace race = new(sessions) { Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; } };

        (SuccessorRecord record, IReadOnlyList<Step> steps) = await race.RunAsync(Orders, attempt: 1, Ct);

        Assert.Equal(["o3-sign-in", SuccessorRace.BeforeStep, "refresh", "logout"], sessions.Sent);
        Assert.Equal(["o3-sign-in", SuccessorRace.BeforeStep, "refresh", "logout"], steps.Select(step => step.Name));
        Assert.All(sessions.Paths, path => Assert.Equal("/fragments/orders/api/orders/whoami", path));
        Assert.Equal([FragmentsWorkloads.DueAfter, SuccessorRace.LogoutLags[0]], waits);
        Assert.Equal(new SuccessorRecord("", "orders"), record);
    }

    [Fact]
    public async Task Each_attempt_sends_the_logout_at_the_next_lag_after_the_refresh()
    {
        // Mutation: one fixed lag in SuccessorRace.LagOf fails the later attempts' waits.
        foreach ((int attempt, TimeSpan lag) in new[] { (1, SuccessorRace.LogoutLags[0]), (2, SuccessorRace.LogoutLags[1]), (SuccessorRace.LogoutLags.Count + 1, SuccessorRace.LogoutLags[0]) })
        {
            List<TimeSpan> waits = [];
            await new SuccessorRace(new Sessions()) { Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; } }.RunAsync(Orders, attempt, Ct);
            Assert.Equal(lag, waits[^1]);
        }

        Assert.Equal(SuccessorRace.LogoutLags.Count, SuccessorRace.LogoutLags.Distinct().Count());
    }

    [Fact]
    public async Task A_sign_in_that_did_not_end_signed_in_races_nothing_and_the_row_is_not_proved()
    {
        // Mutation: racing with no cookies in SuccessorRace.RunAsync throws, and this fails.
        Sessions sessions = new() { SignedIn = false };
        SuccessorRace race = new(sessions) { Delay = (_, _) => Task.CompletedTask };

        (SuccessorRecord record, IReadOnlyList<Step> steps) = await race.RunAsync(Orders, attempt: 1, Ct);

        Assert.Equal(["o3-sign-in"], sessions.Sent);
        RowResult result = FragmentsObserver.Successor(steps, record, await FragmentsObserverTests.CaptureAsync());
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("needs a logout step and a refresh step", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_reference_is_read_from_the_request_line_of_the_call_before_the_race()
    {
        // Mutation: reading the session reference from the logout step's line instead fails, since that line carries none here.
        Step before = new(SuccessorRace.BeforeStep, ClientTrace.Fresh().TraceId, FragmentsObserverTests.SignedIn, 200) { Route = "api" };
        Step logout = new("logout", ClientTrace.Fresh().TraceId, FragmentsObserverTests.SignedIn.AddMinutes(6), 302) { Route = "bff" };
        GatewayCapture capture = await FragmentsObserverTests.CaptureAsync(
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskB, before.At, FragmentsObserverTests.RequestLine(before, "api", "5e5510a0f00d")),
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, logout.At, FragmentsObserverTests.RequestLine(logout)));

        Assert.Equal("5e5510a0f00d", SuccessorRace.WithSession(new SuccessorRecord("", "orders"), [before, logout], capture).SessionRef);
        Assert.Equal("", SuccessorRace.WithSession(new SuccessorRecord("", "orders"), [logout], capture).SessionRef);
    }
}
