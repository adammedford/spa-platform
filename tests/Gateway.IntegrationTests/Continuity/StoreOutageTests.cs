using System.Net;
using Gateway.TestHarness;
using static Gateway.IntegrationTests.Continuity.Logins;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// #332, F17 composed (#345): with the cell's one session store failing every call, a request that needs
/// the session ends on the gateway's generic 500, no cookie is cleared, and <c>/healthz</c> and
/// <c>/readyz</c>, which read no store, still answer on every host. T4 pins the 500 in the identity
/// suite; this is the same outcome through the composed pipeline.
/// </summary>
public sealed class StoreOutageTests
{
    [Fact]
    public async Task P2_5_F17_with_the_session_store_failing_a_session_request_ends_500_no_cookie_is_cleared_and_health_still_answers()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        HarnessApp app = harness.App("app1");
        string sessionCookie = Contract.PlatformCookie("-session");
        GatewayHost target = harness.Hosts[0];
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Ct);

        harness.SessionStore!.Failing = true;
        List<BrowserResponse> failed = [];
        foreach (GatewayHost host in harness.Hosts)
        {
            target = host;
            failed.Add(await browser.NavigateAsync(app.Url("/"), Manual, Ct));
            failed.Add(await browser.GetAsync("/bff/session", ct: Ct));
        }

        List<(HttpStatusCode, long?, string?)> ready = [];
        List<HttpStatusCode> live = [];
        foreach (GatewayHost host in harness.Hosts)
        {
            ready.Add(await host.ReadyzAsync(Ct));

            // Each host's /healthz through its own route, so "on both hosts" does not rest on the
            // balancer's alternation.
            using TestBrowser probe = harness.CreateBrowser(() => host);
            live.Add((await probe.NavigateAsync("https://cell.spa-poc.test:8443/healthz", ct: Ct)).StatusCode);
        }

        Assert.Equal(harness.Hosts.Count, live.Count);

        int refused = harness.SessionStore.Refused;
        harness.SessionStore.Failing = false;
        target = harness.Hosts[1];
        BrowserResponse recovered = await browser.GetAsync("/bff/session", ct: Ct);

        // Mutation that turns it red: leaving the store working (Failing never set) answers 200; a gateway
        // that treated a store failure as "no session" would answer 401 or send the browser to the
        // provider and clear the cookie.
        Assert.All(failed, response => Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode));
        Assert.All(failed, response => Assert.DoesNotContain(
            response.SetCookies, line => line.StartsWith(sessionCookie + "=", StringComparison.Ordinal)));
        Assert.True(refused >= failed.Count, $"the store refused {refused} call(s) for {failed.Count} request(s).");

        Assert.Equal([(HttpStatusCode.OK, 1L, "host-a"), (HttpStatusCode.OK, 1L, "host-b")], ready);
        Assert.All(live, status => Assert.Equal(HttpStatusCode.OK, status));

        // The cookie was never cleared, so the session is there again once the store is.
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
