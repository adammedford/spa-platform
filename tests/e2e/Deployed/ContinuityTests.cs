using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed continuity class (#358): #348's rows 3 to 9 on the two-task cell, one test per row,
/// each the driver (<see cref="ContinuityDriver"/>) and the observer (<see cref="ContinuityObserver"/>)
/// with the row's evidence in <c>SPA_E2E_EVIDENCE</c>. With <c>SPA_E2E_STACK</c> unset or
/// <c>local</c> every row reports skipped, naming the switch (Q139); the observer's rules are proved
/// against recordings in <see cref="ContinuityObserverTests"/>, which run everywhere.
///
/// <para>A sitting runs one row per invocation (#361 Q8, #442 Q169), selected by its method, so wiz's
/// sitting D can ask its own approval before rows 4, 6 and 7:
/// <c>dotnet test --project tests/e2e -- --filter-method "Gateway.E2ETests.Deployed.ContinuityTests.Row3_*"</c>,
/// with <c>SPA_E2E_STACK=deployed</c>, <c>SPA_E2E_EVIDENCE</c> naming an ignored directory, and for
/// row 4 <c>SPA_TUNNEL_TASK</c> naming the task the tunnel's hop runs on.</para>
///
/// <para>Each row's first step is the control (Q143): one raw request whose client trace id must be
/// found in exactly one gateway stream, or the row is not proved. A not-proved row is a red test whose
/// message starts <c>not proved:</c>; a broken rule's starts <c>failed:</c> (Q138).</para>
/// </summary>
public sealed class ContinuityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string, string)[] SessionPair = [("session-1", "session-2")];

    [Fact]
    public async Task Row3_a_session_created_on_one_task_is_decrypted_and_used_on_the_other()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        (Profile profile, SessionCookies cookies) = await driver.SignInAsync(driver.App1);
        await using (profile)
        {
            RowResult result = await driver.Run.TwoTaskRowAsync("row3", control, null,
                async (n, ct) => new Attempt(n,
                [
                    await driver.SendAsync("session-1", HttpMethod.Get, "/bff/session", cookies, 200, ct),
                    await driver.SendAsync("session-2", HttpMethod.Get, "/bff/session", cookies, 200, ct),
                ]),
                (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, SessionPair),
                Ct);
            result.Assert();
        }
    }

    [Fact]
    public async Task Row4_after_stop_task_on_the_other_task_the_session_continues_on_the_survivor_and_the_replacement()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        string survivor = Environment.GetEnvironmentVariable(ContinuityEcs.TunnelTaskVariable) ?? "";
        string stopped = "";
        (Profile profile, SessionCookies cookies) = await driver.SignInAsync(driver.App1);
        await using (profile)
        {
            RowResult result = await driver.Run.TwoTaskRowAsync("row4", control,
                async ct => stopped = await driver.Ecs.StopTheOtherAsync(survivor, ct),
                async (n, ct) =>
                {
                    List<Step> calls = [];
                    for (int i = 1; i <= 30; i++)
                    {
                        calls.Add(await driver.SendAsync($"call-{n}-{i}", HttpMethod.Get, "/bff/session", cookies, 200, ct));
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }

                    return new Attempt(n, calls);
                },
                (attempts, capture) => ContinuityObserver.Replacement(survivor, stopped, attempts, capture),
                Ct);
            result.Assert();
        }
    }

    [Fact]
    public async Task Row5_login_state_and_the_antiforgery_token_cross_tasks()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.TwoTaskRowAsync("row5", control, null,
            async (n, _) =>
            {
                (Profile profile, Step start, Step callback) = await driver.LoginAsync(driver.App1);
                await using (profile)
                {
                    (Step session, Step post) = await ContinuityDriver.AntiforgeryAsync(profile, driver.App1);
                    return new Attempt(n, [start, callback, session, post]);
                }
            },
            (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, [("login-start", "callback"), ("session", "post")]),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task Row6_a_rolling_deployment_refreshes_once_inside_the_interval_with_no_401_and_no_5xx()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.OnceAsync("row6", control,
            ct => driver.RollingDeployAsync(TimeSpan.FromMinutes(40), ct),
            deploy => [.. deploy.Sessions.SelectMany(calls => calls), .. new[] { deploy.LoginStart, deploy.LoginCallback }.OfType<Step>()],
            ContinuityObserver.Rolling,
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task Row7_a_cross_task_login_to_an_onboarded_app_after_convergence()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        HarnessApp? app3 = null;
        try
        {
            RowResult result = await driver.Run.TwoTaskRowAsync("row7", control,
                async _ => app3 = await driver.OnboardAsync("app3"),
                async (n, _) =>
                {
                    (Profile profile, Step start, Step callback) = await driver.LoginAsync(app3!);
                    await using (profile)
                    {
                        return new Attempt(n, [start, callback]);
                    }
                },
                (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, [("login-start", "callback")]),
                Ct);
            result.Assert();
        }
        catch (Exception failure)
        {
            // F-07's offboarding leaves the registry as it was (#348 row 7), on its own bound, and runs
            // when the row fails too. A clean-up that fails as well is reported beside the row's own
            // failure, not in place of it, as F-07 does (CODING_STANDARDS.md "A check fails closed", f234abc).
            try
            {
                await driver.OffboardAsync("app3");
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(failure, cleanup);
            }

            throw;
        }

        await driver.OffboardAsync("app3");
    }

    [Fact]
    public async Task Row8_fifty_calls_on_a_due_token_across_two_tasks_refresh_once()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        (Profile profile, SessionCookies cookies) = await driver.SignInWithDueTokenAsync(driver.App1);
        await using (profile)
        {
            RowResult result = await driver.Run.OnceAsync("row8", control,
                async ct => (IReadOnlyList<Step>)await Task.WhenAll(Enumerable.Range(1, ContinuityBounds.Row8Calls)
                    .Select(i => driver.SendAsync($"call-{i}", HttpMethod.Get, "/api/orders/whoami", cookies, 200, ct))),
                calls => calls,
                ContinuityObserver.FiftyCalls,
                Ct);
            result.Assert();
        }
    }

    [Fact]
    public async Task Row9_a_session_ended_on_one_task_is_refused_by_the_other()
    {
        await using ContinuityDriver driver = await ContinuityDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.TwoTaskRowAsync("row9", control, null,
            async (n, ct) =>
            {
                (Profile profile, SessionCookies cookies) = await driver.SignInAsync(driver.App1);
                await using (profile)
                {
                    Step before = await driver.SendAsync("pre-logout-replay", HttpMethod.Get, "/bff/session", cookies, null, ct);
                    Step logout = await driver.LogoutAsync(profile, driver.App1);
                    Step replay = await driver.SendAsync("replay", HttpMethod.Get, "/bff/session", cookies, Contract.Error("unauthenticated").Status, ct);
                    return new Attempt(n, [before, logout, replay]);
                }
            },
            (attempts, capture) => ContinuityObserver.Crossing(attempts, capture, [("logout", "replay")], ["pre-logout-replay"]),
            Ct);
        result.Assert();
    }
}
