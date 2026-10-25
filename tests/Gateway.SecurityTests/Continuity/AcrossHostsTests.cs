using System.Net;
using System.Text;
using Gateway.SecurityTests.Negative;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Continuity;

/// <summary>
/// Plan row P2-2 across hosts, composed (#345; #332, F2 and T7): the three values the key ring protects
/// (the session cookie, the login state and the antiforgery token) issued by host A are accepted by host
/// B, and by a fresh third host started afterwards, which reads the same ring and sessions. The control
/// is the same exchange over one Parameter Store stub per host: the real ring then makes two rings, and
/// B refuses all three.
/// </summary>
public sealed class AcrossHostsTests
{
    private const string EchoPath = "/api/echo/orders";

    [Fact]
    public async Task P2_2_F2_a_session_cookie_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.TwoHosts, Suite.Ct);
        HarnessApp app = harness.App("app1");
        GatewayHost target = harness.Hosts[0];
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Suite.Ct);

        target = harness.Hosts[1];
        BrowserResponse onB = await SignedIn.SessionAsync(browser);
        target = await harness.StartHostAsync(Suite.Ct);
        BrowserResponse onC = await SignedIn.SessionAsync(browser);

        // Mutation that turns it red: KeyRingSharing.ParameterStorePerHost in place of TwoHosts, which is
        // the control below, or a harness that gives each host its own session store.
        Assert.Equal(HttpStatusCode.OK, onB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onC.StatusCode);
        Assert.Equal("host-c", target.Instance);
        Assert.True(SignedIn.HoldsSessionCookie(browser, app));
    }

    [Fact]
    public async Task P2_2_F2_a_login_started_on_A_finishes_on_B_and_on_a_fresh_third_host()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.TwoHosts, Suite.Ct);
        HarnessApp app = harness.App("app1");

        GatewayHost target = harness.Hosts[0];
        using TestBrowser toB = harness.CreateBrowser(() => target);
        Uri callback = await SignedIn.UpToCallbackAsync(harness, toB, app);
        target = harness.Hosts[1];
        BrowserResponse landedOnB = await toB.NavigateAsync(callback, ct: Suite.Ct);

        GatewayHost third = await harness.StartHostAsync(Suite.Ct);
        GatewayHost targetC = harness.Hosts[0];
        using TestBrowser toC = harness.CreateBrowser(() => targetC);
        Uri callbackC = await SignedIn.UpToCallbackAsync(harness, toC, app);
        targetC = third;
        BrowserResponse landedOnC = await toC.NavigateAsync(callbackC, ct: Suite.Ct);

        // Mutation that turns it red: one Parameter Store stub per host (the control below): B cannot
        // unprotect A's correlation and nonce cookies, and the callback never reaches the shell.
        AssertLandedOnShell(app, landedOnB);
        AssertLandedOnShell(app, landedOnC);
    }

    [Fact]
    public async Task P2_2_F2_an_antiforgery_token_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.TwoHosts, Suite.Ct);
        GatewayHost target = harness.Hosts[0];
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string token = await SignedIn.CsrfTokenAsync(browser);
        int reached = harness.Echo.Requests.Count;

        target = harness.Hosts[1];
        BrowserResponse onB = await PostAsync(browser, token);
        target = await harness.StartHostAsync(Suite.Ct);
        BrowserResponse onC = await PostAsync(browser, token);

        // Mutation that turns it red: the control's two rings; B then refuses the pair.
        Assert.Equal(HttpStatusCode.OK, onB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onC.StatusCode);
        Assert.Equal(reached + 2, harness.Echo.Requests.Count);
    }

    /// <summary>
    /// The control that makes the three rows above able to fail (CODING_STANDARDS.md): the same harness
    /// with one Parameter Store stub per host. The real ring then mints a key in each, and B refuses the
    /// session cookie, the login state and the antiforgery token A issued.
    /// </summary>
    [Fact]
    public async Task P2_2_F2_control_over_two_parameter_store_stubs_B_refuses_all_three()
    {
        await using GatewayHarness harness = await Suite.StartAsync(
            GatewayHarnessOptions.TwoHosts with { KeyRing = KeyRingSharing.ParameterStorePerHost }, Suite.Ct);
        HarnessApp app = harness.App("app1");
        Assert.NotSame(harness.Hosts[0].ParameterStore, harness.Hosts[1].ParameterStore);

        // The session cookie and the antiforgery token.
        GatewayHost target = harness.Hosts[0];
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string token = await SignedIn.CsrfTokenAsync(browser);
        int reached = harness.Echo.Requests.Count;
        target = harness.Hosts[1];
        BrowserResponse session = await SignedIn.SessionAsync(browser);
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse post = await PostAsync(browser, token);
        string? postRule = await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct);

        // The login state.
        GatewayHost loginTarget = harness.Hosts[0];
        using TestBrowser login = harness.CreateBrowser(() => loginTarget);
        Uri callback = await SignedIn.UpToCallbackAsync(harness, login, app);
        loginTarget = harness.Hosts[1];
        before = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse callbackOnB = await login.NavigateAsync(callback, SignedIn.Manual, Suite.Ct);
        string? callbackRule = await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct);

        // B cannot unprotect the session cookie: 401 unauthenticated, as a cookie naming no session is.
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, session.StatusCode);

        // The antiforgery stage runs before the proxy's token step, so the 403 is the token pair's own
        // refusal and not the session's, and nothing reached the upstream.
        Assert.Equal((HttpStatusCode)Contract.Error("csrf_failed").Status, post.StatusCode);
        Assert.Equal(RuleIds.CsrfToken, postRule);
        Assert.Equal(reached, harness.Echo.Requests.Count);

        // B cannot unprotect the correlation cookie or the state: the callback's failure page.
        Assert.Equal(HttpStatusCode.BadRequest, callbackOnB.StatusCode); // Not an Errors row: the callback's failure page (Errors prose).
        Assert.Equal(RuleIds.AuthCallback, callbackRule);
        Assert.False(SignedIn.HoldsSessionCookie(login, app));
    }

    private static void AssertLandedOnShell(HarnessApp app, BrowserResponse landed)
    {
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.True(FetchMetadata.IsSameOrigin(app.Origin, landed.Url), $"the login ended at {landed}");
        Assert.Equal(FixtureFiles.Spa(app.AppId)["index.html"], landed.Bytes);
    }

    private static Task<BrowserResponse> PostAsync(TestBrowser browser, string token) =>
        browser.FetchAsync(
            HttpMethod.Post,
            EchoPath,
            new StringContent("{\"order\":1}", Encoding.UTF8, "application/json"),
            RequestOptions.With((Contract.CsrfHeaderName, token)),
            Suite.Ct);
}
