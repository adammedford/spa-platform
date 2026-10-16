using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// Plan row 6.3, the negative suite N-01 to N-25 and N-27, exercised through the composed gateway.
/// Each test observes a client response, rendered logs, the session store or an upstream's record
/// rather than invoking middleware directly (docs/plan/04-tests.md).
///
/// <para>Two kinds of cell. The sessionless rows (task 6.3a) run on the gateway as <c>Program.cs</c>
/// composes it. Every row that needs a session runs on a cell that requires one
/// (<see cref="SignedIn.StartAsync"/>), with a browser signed in through the test provider, and a
/// row whose sessionless half landed first (N-07, N-10, N-12, N-24, N-27) has its session half beside
/// it in the same class. N-25's browser clause and N-26 rest on the browser enforcing the
/// <c>__Host-</c> prefix, which the test browser does not do (R-D3); the deployed suite holds
/// them.</para>
///
/// <para>Canary coverage spans this project's harnesses through <see cref="StartAsync"/> and the
/// assembly's final <see cref="SuiteSweep"/>.</para>
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
