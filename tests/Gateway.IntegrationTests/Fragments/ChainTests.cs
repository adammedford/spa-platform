using System.Net;
using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// The eager chain on the composed gateway (#1007, part of #961 -> "The eager chain"): one login to a
/// shell gives the shell and every fragment a token set, each from its own client at the test
/// provider, through <c>/signin-oidc/{fragmentId}</c> and <c>/bff/chain</c>, and lands on
/// <c>returnUrl</c>. Workstream 3 proved the same against its doubles (FragmentChainTests in
/// tests/Platform.Identity.Tests); here it runs through the real composition.
/// </summary>
public sealed class ChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ReturnUrl = "/orders/42";

    /// <summary>
    /// F-24: at N = 0, 1 and 5, one <c>/bff/login</c> ends on <c>returnUrl</c> after one interactive
    /// sign-in, with 4 + 3N redirects (3 at N = 0); <c>/bff/session</c> reads <c>signedIn</c> for every fragment, and
    /// each fragment's API receives a token issued to that fragment's client.
    /// </summary>
    [Fact]
    public async Task G9_one_login_chains_every_fragment_and_lands_on_returnUrl_at_N_0_1_and_5()
    {
        foreach (int n in (int[])[0, 1, 5])
        {
            await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(n), Ct);
            HarnessApp shell = harness.App("shell");
            Assert.Equal(n, shell.Fragments.Length);

            // The provider keeps a session, so an interactive sign-in and a silent prompt=none hop are
            // told apart at the provider (#997).
            harness.Idp.Faults.ModelProviderSession = true;
            using TestBrowser browser = harness.CreateBrowser();

            BrowserResponse landed = await Fragments.LoginAsync(browser, shell, ReturnUrl, Ct);

            // 4 + 3N redirects at N >= 1: /bff/login, the shell's authorization, its callback and the first
            // /bff/chain step, then each fragment's authorization, callback and next step. At N = 0 the
            // shell's callback lands on returnUrl itself, since a shell with no fragments has no chain:
            // 3, not the register row's 4 (decided on #1007,
            // https://github.com/adammedford/spa-platform/issues/1007#issuecomment-7159533460).
            // Mutation that turns it red: a chain that stops after the shell's callback lands with 4
            // redirects at every N, and the N = 1 and N = 5 counts fail.
            Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
            Assert.Equal(shell.Url(ReturnUrl), landed.Url);
            Assert.Equal(n == 0 ? 3 : 4 + (3 * n), landed.RedirectedFrom.Length);

            // One interactive sign-in, the shell's; each fragment's hop is its own client's silent
            // authorization, in registry order. Mutation: a chain hop that skipped a fragment drops its
            // client here; a hop sent without prompt=none counts as a second interactive sign-in.
            string[] clients = [shell.ClientId, .. shell.Fragments.Select(fragment => fragment.ClientId)];
            Assert.Equal(clients, harness.Idp.Authorizations.Where(a => a.Ok).Select(a => a.ClientId));
            TestIdp.Authorization interactive = Assert.Single(harness.Idp.Authorizations, a => !a.Silent);
            Assert.Equal(shell.ClientId, interactive.ClientId);
            Assert.Equal(
                clients,
                harness.Idp.Grants.Where(g => g.GrantType == "authorization_code" && g.Ok).Select(g => g.ClientId));

            JsonElement session = await Fragments.SessionAsync(browser, shell, Ct);
            Assert.Equal("alice", session.GetProperty("sub").GetString());
            Dictionary<string, string> states = Fragments.States(session);
            Assert.Equal(shell.Fragments.Select(fragment => fragment.FragmentId).Order(), states.Keys.Order());
            Assert.All(states.Values, state => Assert.Equal("signedIn", state));

            // Each fragment's API receives a token its own client was issued, not the shell's.
            foreach (HarnessFragment fragment in shell.Fragments)
            {
                string clientId = await Fragments.ClientIdAtEchoAsync(harness, browser, shell, fragment.FragmentId, Ct);
                Assert.Equal(fragment.ClientId, clientId);
            }

            Assert.Equal(shell.ClientId, await Fragments.ClientIdAtEchoAsync(harness, browser, shell, fragmentId: null, Ct));
        }
    }

    /// <summary>
    /// F-25: with one fragment's client secret wrong, the chain continues past it and lands on
    /// <c>returnUrl</c>; the shell's <c>index.html</c> is served; <c>/bff/session</c> reads
    /// <c>unavailable</c> for that fragment and <c>signedIn</c> for the other.
    /// </summary>
    [Fact]
    public async Task G9_a_fragment_whose_client_is_refused_reads_unavailable_and_the_shell_still_loads()
    {
        // Mutation that turns it red: a hop that stops the chain on a definitive refusal lands on the
        // orders callback rather than returnUrl, and reports is never chained; a refusal read as transient
        // leaves orders loginRequired, not unavailable.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = shell.Fragment("orders");

        // The provider now holds another secret for orders' client than the one the gateway reads: its
        // code exchange is answered invalid_client, a definitive refusal.
        harness.Idp.SetClientSecret(orders.ClientId, "not-" + orders.ClientSecret);
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse landed = await Fragments.LoginAsync(browser, shell, "/", Ct);

        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(shell.Url("/"), landed.Url);
        Assert.Equal(FixtureFiles.Spa("shell")["index.html"], landed.Bytes);
        Assert.Equal(
            "invalid_client",
            Assert.Single(harness.Idp.Grants, g => g.GrantType == "authorization_code" && g.ClientId == orders.ClientId).Outcome);
        Assert.Equal(
            new Dictionary<string, string> { ["orders"] = "unavailable", ["reports"] = "signedIn" },
            Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
    }

    /// <summary>
    /// F-43, <c>login_required</c>: with TestIdp's fidelity switch on and no provider session when orders'
    /// hop reaches the provider, the hop is answered <c>login_required</c> and no interactive request
    /// follows for orders; the chain continues past it to <c>returnUrl</c>, and <c>/bff/session</c> reads
    /// <c>loginRequired</c> for orders, not <c>unavailable</c>.
    /// </summary>
    [Fact]
    public async Task G9_a_fragment_whose_hop_is_answered_login_required_reads_loginRequired_and_the_chain_continues()
    {
        // Mutation that turns it red: reading login_required as a definitive refusal marks orders
        // unavailable; a hop that retries interactively on login_required makes a non-silent
        // authorization for orders.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        harness.Idp.Faults.ModelProviderSession = true;
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = shell.Fragment("orders");
        using TestBrowser browser = harness.CreateBrowser();

        // Step to orders' hop at the provider, and send it there without the provider session cookie the
        // shell's interactive sign-in set, as a browser whose provider session ended in between would.
        BrowserResponse start = await Steps.ToAsync(browser, shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(ReturnUrl)), Ct);
        BrowserResponse before = await Steps.UntilAsync(browser, start, url => Steps.IsAuthorizeFor(url, orders.ClientId), Ct);
        Uri authorize = Steps.Next(before)!;
        System.Net.Cookie session = Assert.Single(
            browser.AllCookies, cookie => cookie.Name == TestIdp.TestIdentityProvider.SessionCookieName);
        session.Expired = true;
        BrowserResponse answered = await Steps.ToAsync(browser, authorize, Ct);

        // The provider session is back for the rest of the chain, so only orders' hop met none.
        browser.Cookies.Add(new System.Net.Cookie(session.Name, session.Value, session.Path, session.Domain));
        BrowserResponse landed = await browser.NavigateAsync(Steps.Next(answered)!, ct: Ct);

        TestIdp.Authorization hop = Assert.Single(harness.Idp.Authorizations, a => a.ClientId == orders.ClientId);
        Assert.Equal((TestIdp.Authorization.LoginRequired, true), (hop.Outcome, hop.Silent));
        Assert.Equal(shell.Url(ReturnUrl), landed.Url);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(
            new Dictionary<string, string> { ["orders"] = "loginRequired", ["reports"] = "signedIn" },
            Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
    }

    /// <summary>
    /// F-43, transient: with orders' hop meeting a transient provider failure in place of
    /// <c>login_required</c> (its ID token signed with a key the provider never published, a failure with
    /// no OAuth error code), the chain continues past it to <c>returnUrl</c>, and <c>/bff/session</c> reads
    /// <c>loginRequired</c> for orders, not <c>unavailable</c>.
    /// </summary>
    [Fact]
    public async Task G9_a_fragment_whose_hop_fails_transiently_reads_loginRequired_and_the_chain_continues()
    {
        // Mutation that turns it red: reading a failure with no OAuth error code as definitive marks
        // orders unavailable; a hop that stops the chain on it never reaches reports or returnUrl.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        harness.Idp.Faults.ModelProviderSession = true;
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = shell.Fragment("orders");
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse start = await Steps.ToAsync(browser, shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(ReturnUrl)), Ct);
        BrowserResponse before = await Steps.UntilAsync(browser, start, url => url.AbsolutePath == "/signin-oidc/orders", Ct);

        // Only orders' code exchange is answered with the unpublished key.
        harness.Idp.Faults.SignWithUnpublishedKey = true;
        BrowserResponse callback = await Steps.ToAsync(browser, Steps.Next(before)!, Ct);
        harness.Idp.Faults.SignWithUnpublishedKey = false;
        BrowserResponse landed = await browser.NavigateAsync(Steps.Next(callback) ?? throw new InvalidOperationException($"orders' callback answered {callback}"), ct: Ct);

        Assert.Equal(shell.Url(ReturnUrl), landed.Url);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(
            new Dictionary<string, string> { ["orders"] = "loginRequired", ["reports"] = "signedIn" },
            Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
    }
}
