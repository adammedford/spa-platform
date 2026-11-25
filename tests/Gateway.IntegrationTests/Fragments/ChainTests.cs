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
}
