using System.Net;
using Gateway.TestHarness;
using Platform.Registry;
using static Gateway.IntegrationTests.Continuity.Logins;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// Plan row P2-5's hermetic proof (change request P2-5, "The proof"; #332, F8 and D3; #345): onboard app
/// 3 and poll host A only. A's <c>/readyz</c> says 2 and B's says 1; <c>spactl wait</c> with both
/// instance ids returns only once B has been polled, and one with an earlier deadline times out naming
/// B. A login to app 3 started on A finishes on B after convergence, and before it (D3's control) the
/// callback on B ends 404 <c>not_found</c>, because B does not know the host yet.
/// </summary>
public sealed class ConvergenceTests
{
    [Fact]
    public async Task P2_5_F8_readyz_shows_the_skew_and_spactl_wait_returns_only_after_B_is_polled_and_a_shorter_wait_times_out_naming_B()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];

        (_, RefreshOutcome onboarded) = await harness.OnboardAsync("app3", Ct);
        Assert.Equal(RefreshOutcome.Swapped, onboarded);
        Assert.Equal((HttpStatusCode.OK, 2L, "host-a"), await a.ReadyzAsync(Ct));
        Assert.Equal((HttpStatusCode.OK, 1L, "host-b"), await b.ReadyzAsync(Ct));

        // A wait whose deadline falls before B is polled times out naming B, and only B. Three seconds at
        // a 20 ms poll is many reads of each host even on a loaded machine.
        using CountingHandler shortHttp = new(harness.CreateLoadBalancer());
        StringWriter shortOut = new(), shortError = new();
        int timedOut = await WaitAsync(shortHttp, shortOut, shortError, Ct, "--revision", "2", "--instances", "host-a,host-b", "--timeout", "3");

        // Mutation that turns it red: a wait that ends on the first answer at the revision (dropping
        // --instances here) returns 0 after host-a's first read.
        Assert.Equal(1, timedOut);
        Assert.Contains("instance host-b last answered revision 1", shortError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("instance host-a", shortError.ToString(), StringComparison.Ordinal);
        Assert.True(shortHttp.Sent >= 2, $"the wait read /readyz {shortHttp.Sent} time(s), so it never sampled both hosts.");

        // A wait with time to spare does not return while B is behind, however often it has read both.
        using CountingHandler http = new(harness.CreateLoadBalancer());
        StringWriter output = new(), error = new();
        Task<int> wait = WaitAsync(http, output, error, Ct, "--revision", "2", "--instances", "host-a,host-b", "--timeout", "60");
        while (http.Sent < 10 && !wait.IsCompleted)
        {
            await Task.Delay(10, Ct);
        }

        // Mutation that turns it red: polling B before this line (b.RefreshRegistryAsync moved up), or a
        // balancer that sends every read to A and so never shows the wait B's 1.
        Assert.False(wait.IsCompleted, $"the wait returned before B was polled: {output}{error}");

        Assert.Equal(RefreshOutcome.Swapped, await b.RefreshRegistryAsync(Ct));
        int ready = await wait.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(0, ready);
        Assert.Contains("instance host-a at revision 2, instance host-b at revision 2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task P2_5_F8_a_login_to_the_onboarded_app_started_on_A_ends_404_on_a_lagging_B_and_finishes_on_B_after_convergence()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        (HarnessApp app3, _) = await harness.OnboardAsync("app3", Ct);

        // The control (D3's reading): B still at 1 does not know app 3's host.
        GatewayHost target = a;
        using TestBrowser early = harness.CreateBrowser(() => target);
        Uri earlyCallback = await UpToCallbackAsync(harness, early, app3);
        target = b;
        BrowserResponse lagging = await early.NavigateAsync(earlyCallback, Manual, Ct);

        // Mutation that turns it red: polling B before the callback (convergence first) lands the control
        // on the shell instead of 404.
        Assert.Equal((HttpStatusCode)Contract.Error("not_found").Status, lagging.StatusCode);
        Assert.Equal("not_found", Code(lagging));

        // After convergence the same journey succeeds: started on A, finished on B.
        Assert.Equal(RefreshOutcome.Swapped, await b.RefreshRegistryAsync(Ct));
        GatewayHost route = a;
        using TestBrowser browser = harness.CreateBrowser(() => route);
        Uri callback = await UpToCallbackAsync(harness, browser, app3);
        route = b;
        BrowserResponse landed = await browser.NavigateAsync(callback, ct: Ct);

        // Mutation that turns it red: KeyRingSharing.ParameterStorePerHost (B cannot read A's login
        // state), or leaving B unpolled (404 again).
        AssertOnShell(app3, landed);
    }
}
