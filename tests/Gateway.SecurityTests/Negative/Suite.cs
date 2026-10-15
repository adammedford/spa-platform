using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The negative suite of plan row 6.3, carved as task 6.3a: every row of
/// <c>docs/plan/04-tests.md</c> that needs no session, proven in the <b>composed</b> gateway rather
/// than in the project that implements the refusal.
///
/// <para><b>Rows proven here.</b> Whole: N-08, N-09, N-13, N-14, N-16, N-20, N-24, N-25, N-27.
/// Partial, with the unproven clause named in the test that proves the rest: N-07, N-10, N-12. A
/// partial row says in its own class which clause waits and for what, because a row recorded as
/// whole when half of it is unproven is worse than a row left out.</para>
///
/// <para><b>Rows absent, not skipped.</b> N-01 to N-06, N-15, N-17, N-18, N-19, N-21, N-22 and N-23
/// all need a session, and nothing in <c>src/Platform.Identity</c> starts before <c>contracts-v1</c>
/// is tagged (AGENTS.md). N-26 and the <c>__Host-</c>-prefix clause of N-25 need a real browser and
/// belong to the deployed suite of row 6.5. A skipped test reports a count that reads like coverage,
/// so they are not here at all; they join this project and this harness at G4, which is row 6.3's
/// own done-when.</para>
///
/// <para><b>N-11 is absent for a different reason, and this is the one surprise in the carve.</b> It
/// needs no session for the clause that matters — "the upstream sees none of" the caller's
/// <c>Cookie</c>, bearer token, CSRF header and forwarding headers — but nothing strips them yet.
/// Plan row <b>4.2</b> owns that ("Strip <c>Cookie</c>, client <c>Authorization</c>, the CSRF header
/// and the browser's forwarding headers... Strip <c>Set-Cookie</c> and <c>Access-Control-*</c> from
/// responses on every status") and its needs are "4.1, G1b", so it has not landed. Measured on this
/// branch through the harness's echo upstream, an <c>/api</c> call's <c>Cookie</c>,
/// <c>Authorization</c>, <c>X-CSRF-TOKEN</c>, <c>Via</c>, <c>X-Real-IP</c> and <c>X-Original-URL</c>
/// all reach the upstream, and an upstream's <c>Set-Cookie</c> reaches the browser's jar. That is
/// not a defect this lane may fix (workstream 6 owns the proof, not the stages) and not a defect to
/// report either, because the plan says where the work lives. So N-11 waits for 4.2 rather than
/// being written red, and <see cref="UpstreamResponseTests"/> holds the same note for N-12's
/// <c>Set-Cookie</c> clause, beside the upstream that will prove both.</para>
///
/// <para><b>The temporal caveat, once, for the whole suite.</b> The shell gate is stage 8 of
/// <c>contracts/pipeline.md</c> and belongs to workstream 3, which has not landed it:
/// <c>UsePlatformShellGate</c> is a pass-through today, so an asset and an <c>/api</c> path are both
/// reachable with no session. Every row here that reaches one does so <b>because that stage is
/// absent</b>. When it lands, ADR-0008 makes an unauthenticated navigation a challenge and an
/// unauthenticated fetch a 401, and the status codes these tests read change <b>legitimately</b>.
/// The refusals themselves are stages 1 to 6 and stage 10, all of which run before or instead of
/// stage 8, so the refusal assertions survive; it is the <i>reached</i> responses (a 200 shell, a
/// 200 from an upstream) that are to be re-read then, not treated as a regression. Each class that
/// depends on this says so again where it matters.</para>
///
/// <para><b>What a test here may assert.</b> Only what a client outside the gateway can observe: the
/// status code, the response headers, the body, the log lines the run produced, and what an upstream
/// received. No test calls a stage class, constructs a middleware or reads a private field — a row
/// that would need to is one that belongs in its owner's own project, where it already is.</para>
/// </summary>
internal static class Suite
{
    /// <summary>
    /// A harness for one test class, with every line it logs and every client secret it minted
    /// enrolled in the suite-wide canary sweep (N-16). Every harness in this project is started
    /// through here, which is what lets that row's claim be about the whole run rather than a sample:
    /// a class that called <see cref="GatewayHarness.StartAsync"/> itself would produce lines nothing
    /// sweeps.
    /// </summary>
    public static async Task<GatewayHarness> StartAsync(GatewayHarnessOptions? options = null, CancellationToken ct = default)
    {
        GatewayHarness harness = await GatewayHarness.StartAsync(options, ct);
        CanarySweep.Watch(harness);
        return harness;
    }

    /// <summary>The cancellation token of the running test, as xUnit v3 supplies it.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
