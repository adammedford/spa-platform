using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// Plan row 6.3, the negative suite N-01 to N-25 and N-27, exercised through the composed gateway.
/// Each test observes a client response, rendered logs, the session store or an upstream's record
/// rather than invoking middleware directly (docs/plan/04-tests.md).
///
/// <para>One kind of cell, which requires a session before it serves an asset or forwards an API
/// call, as ADR-0008 decides. The composition that ships does that, not the harness:
/// <c>AddPlatformIdentity</c> binds the token source and <c>UsePlatformShellGate</c> is always in
/// the pipeline. A row that needs a session signs a browser in through the test provider. A row
/// whose request is refused before authentication runs (host resolution, the request gate, Kestrel
/// itself) needs none.
/// A row that sends an asset or API request without one asserts the refusal it gets: a navigation is
/// sent to the provider, any other asset request gets 401 <c>unauthenticated</c> (under
/// <c>auth.session</c> when it presented a session cookie that names no session), and an API call the
/// proxy would forward gets the same 401 from the proxy's token step instead (task 6.3c). Two API
/// answers come from elsewhere, each named by its writer: <c>UsePlatformAntiforgery</c> refuses an
/// unsafe method 403 <c>csrf_failed</c>, since no anonymous token pair exists, and
/// <c>MapPlatformProxy</c>'s catch-all answers a name the app does not list 404 <c>unknown_api</c>,
/// with a session or without. Where a row has a sessionless half and a signed-in half (N-07, N-10,
/// N-27), both are in the same class. N-25's browser clause and N-26 rest on the browser enforcing the
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
