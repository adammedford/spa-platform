using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The session-independent portion of plan row 6.3, task 6.3a, exercised through the composed
/// gateway. Each test observes a client response, rendered logs or the upstream request rather
/// than invoking middleware directly (docs/plan/04-tests.md).
///
/// <para>Whole rows: N-08, N-09, N-13, N-14, N-16 and N-20. Partial rows: N-07, N-10, N-12,
/// N-24, N-25 and N-27. Cookie-prefix enforcement requires a real browser; remote-signout session
/// preservation requires a live session. Those behaviours are outside these requests' evidence.</para>
///
/// <para>Canary coverage spans this project's harnesses through <see cref="StartAsync"/> and the
/// assembly's final <see cref="SuiteSweep"/>. The individual tests' assertions define the coverage;
/// a partial row must not be read as proof of its session or browser clauses.</para>
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
