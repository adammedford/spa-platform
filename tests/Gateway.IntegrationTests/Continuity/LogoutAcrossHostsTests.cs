using System.Net;
using Gateway.TestHarness;
using static Gateway.IntegrationTests.Continuity.Logins;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// Plan row P2-3's second acceptance composed (#346; #332 F6): "a session ended through one task is
/// refused by the other on its next request". Two gateway hosts over one session store, so what B reads on
/// that next request is the store A removed the session from, with no copy of its own in between.
/// </summary>
public sealed class LogoutAcrossHostsTests
{
    [Fact]
    public async Task P2_3_F6_a_session_logged_out_through_A_is_refused_by_B_on_the_next_request()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        HarnessApp app = harness.App("app1");
        GatewayHost target = a;
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Ct);

        // B serves the session A created, on the token path and the shell, before the logout.
        target = b;
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/orders/whoami", ct: Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.NavigateAsync(app.Url("/"), Manual, Ct)).StatusCode);
        string signedIn = browser.Cookies.GetCookieHeader(app.Origin);

        target = a;
        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: Manual, ct: Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        // The next requests to B carry the cookie the browser held before the logout.
        target = b;
        BrowserResponse call = await browser.GetAsync("/api/orders/whoami", RequestOptions.With(("Cookie", signedIn)), Ct);
        BrowserResponse navigation = await browser.NavigateAsync(
            app.Url("/"), Manual with { Headers = new Dictionary<string, string?> { ["Cookie"] = signedIn } }, Ct);

        // Mutation that turns it red: a logout that does not reach the shared store
        // (SwitchableSessionStore.RemoveAsync doing nothing): B still finds the session, the call is 200
        // and the shell is served. 401 unauthenticated, not session_ended: the session is gone, not refused
        // on a refresh (contracts/http-contract.md, Errors).
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, call.StatusCode);
        Assert.Equal("unauthenticated", Code(call));
        Assert.Equal(HttpStatusCode.Redirect, navigation.StatusCode);
        Assert.StartsWith(harness.Idp.AuthorizeEndpoint + "?", navigation.Header("Location"), StringComparison.Ordinal);
        Assert.NotEqual(FixtureFiles.Spa("app1")["index.html"], navigation.Bytes);
    }
}
