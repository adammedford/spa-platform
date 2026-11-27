using System.Net;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// One <c>/bff/chain</c> step on the composed gateway (#1007, part of #961; contracts/http-contract.md
/// -> "The chain step"): what a valid continuation does, by Fetch Metadata and by position.
/// </summary>
public sealed class ChainStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// F-33: a <c>GET /bff/chain</c> step with <c>Sec-Fetch-Site: cross-site</c>, <c>Sec-Fetch-Mode:
    /// navigate</c>, <c>Sec-Fetch-Dest: document</c> and the continuation the previous hop minted
    /// challenges the next fragment's scheme; with <c>same-site</c> and <c>same-origin</c> it does the same.
    /// </summary>
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("same-origin")]
    public async Task G9_a_cross_site_navigation_with_a_valid_continuation_proceeds_to_the_next_fragment(string site)
    {
        // Mutation that turns it red: a step gate that requires same-origin (the BFF's usual rule for a
        // navigation it answers) refuses the cross-site and same-site rows with 403 under gate.navigation.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        Uri step = await Steps.FirstChainStepAsync(browser, shell, "/done", Ct);

        BrowserResponse proceeded = await browser.NavigateAsync(
            step,
            RequestOptions.With(("Sec-Fetch-Site", site), ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"))
                with { Redirect = RedirectMode.Manual },
            Ct);

        Assert.Equal(HttpStatusCode.Redirect, proceeded.StatusCode);
        Uri challenge = Steps.Next(proceeded)!;
        Assert.Equal(harness.Idp.AuthorizeEndpoint, challenge.GetLeftPart(UriPartial.Path));
        Assert.Equal(shell.Fragment("orders").ClientId, Steps.ClientOf(challenge));
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(shell.Url("/signin-oidc/orders").ToString()), challenge.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// F-34: a fragment removed from the entry mid-chain is skipped and the chain lands on
    /// <c>returnUrl</c>; a step replayed after its fragment is <c>signedIn</c> with the current client makes
    /// no provider call and leaves the record's version unchanged.
    /// </summary>
    [Fact]
    public async Task G9_a_step_skips_a_removed_fragment_and_a_replayed_step_does_no_work()
    {
        // Mutation that turns it red: a step that reads the continuation's snapshot without checking the
        // live entry challenges the removed orders (an authorization for its client appears); a step that
        // challenges the position's fragment whatever its state makes a second authorization for orders on
        // the replay and a new record version.
        await using (GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct))
        {
            HarnessApp shell = harness.App("shell");
            using TestBrowser browser = harness.CreateBrowser();
            Uri step = await Steps.FirstChainStepAsync(browser, shell, "/done", Ct);
            Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(
                next => next["apps"]![0]!["fragments"]!.AsArray().RemoveAt(0), Ct));

            BrowserResponse landed = await browser.NavigateAsync(step, ct: Ct);

            Assert.Equal(shell.Url("/done"), landed.Url);
            Assert.DoesNotContain(harness.Idp.Authorizations, a => a.ClientId == shell.Fragment("orders").ClientId);
            Assert.Equal(
                new Dictionary<string, string> { ["reports"] = "signedIn" },
                Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
        }

        await using (GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.Shell(1) with { KeyRing = KeyRingSharing.OneParameterStore }, Ct))
        {
            HarnessApp shell = harness.App("shell");
            using TestBrowser browser = harness.CreateBrowser();
            Uri step = await Steps.FirstChainStepAsync(browser, shell, "/done", Ct);
            BrowserResponse chained = await browser.NavigateAsync(step, ct: Ct);
            Assert.Equal(shell.Url("/done"), chained.Url);
            string key = Assert.Single(harness.SessionStore!.Created);
            long version = (await harness.SessionStore.GetAsync(key, Ct))!.Version;
            int authorizations = harness.Idp.Authorizations.Count;
            int grants = harness.Idp.Grants.Count;

            BrowserResponse replayed = await browser.NavigateAsync(step, ct: Ct);

            Assert.Equal(shell.Url("/done"), replayed.Url);
            Assert.Equal(authorizations, harness.Idp.Authorizations.Count);
            Assert.Equal(grants, harness.Idp.Grants.Count);
            Assert.Equal(version, (await harness.SessionStore.GetAsync(key, Ct))!.Version);
        }
    }
}
