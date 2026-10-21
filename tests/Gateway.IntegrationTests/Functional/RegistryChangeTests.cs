using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-09 (docs/plan/04-tests.md): publish a bad registry, a rotated secret and a removal, while the
/// gateway runs and users are signed in. Through the composed gateway with a session required. Task 6.2.
/// Each test starts its own harness, because each changes the registry.
/// </summary>
public sealed class RegistryChangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"revision\": 2, \"apps\": [")]
    [InlineData("{ \"schemaVersion\": 1, \"revision\": 2, \"apps\": [ { \"appId\": \"app1\" } ] }")]
    public async Task F09_a_bad_registry_document_is_ignored_and_the_live_one_keeps_serving(string bad)
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Ct);

        RefreshOutcome outcome = await harness.PublishRegistryTextAsync(bad, Ct);
        BrowserResponse shell = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", ct: Ct);
        BrowserResponse other = await browser.NavigateAsync(harness.App("app2").Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);

        Assert.Equal(RefreshOutcome.Rejected, outcome);
        Assert.Equal(1, harness.Registry.Current.Revision);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(FixtureFiles.Spa("app1")["index.html"], shell.Bytes);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        // App 2, which the bad document dropped or broke, is still served: it is challenged, not 404.
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Fact]
    public async Task F09_a_rotated_secret_is_used_for_the_next_login_and_the_next_refresh()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser signedIn = harness.CreateBrowser();
        using TestBrowser newcomer = harness.CreateBrowser();
        HarnessApp app = harness.App("app1");

        // A session from before the rotation whose token is already due, so its next call refreshes.
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(signedIn, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);

        // The rotation as an operator runs it: the provider takes the new secret (and stops taking the
        // old one), the new secret is stored under a new reference, and a registry revision names it.
        string rotated = "secret-app1-rotated-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string rotatedRef = "ssm:/spa-platform/harness/apps/app1/client-secret:2";
        harness.Idp.SetClientSecret(app.ClientId, rotated);
        harness.Secrets[rotatedRef] = rotated;
        RefreshOutcome outcome = await harness.UpdateRegistryAsync(
            document => Entry(document, "app1")["oauth"]!["clientSecretRef"] = rotatedRef, Ct);

        BrowserResponse refreshed = await signedIn.GetAsync("/api/orders/whoami", ct: Ct);
        await harness.SignInAsync(newcomer, "app1", Ct);
        BrowserResponse fresh = await newcomer.GetAsync("/api/orders/whoami", ct: Ct);

        Assert.Equal(RefreshOutcome.Swapped, outcome);

        // The provider refuses the old secret now, so each grant below succeeded only with the new one.
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        TokenGrant refresh = Assert.Single(harness.Idp.RefreshGrants);
        Assert.True(refresh.Ok, refresh.ToString());
        Assert.Equal(app.ClientId, refresh.ClientId);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        TokenGrant[] logins = [.. harness.Idp.Grants.Where(grant => grant.GrantType == "authorization_code")];
        Assert.Equal(2, logins.Length);
        Assert.All(logins, grant => Assert.True(grant.Ok, grant.ToString()));
        Assert.DoesNotContain(harness.Idp.Grants, grant => !grant.Ok);
    }

    [Fact]
    public async Task F09_control_without_the_registry_revision_the_old_secret_is_refused()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();

        // The provider rotates and the registry does not follow: the gateway still sends the old secret,
        // and the provider refuses it. This is what makes the previous test's grants evidence.
        harness.Idp.SetClientSecret(harness.App("app1").ClientId, "secret-the-gateway-does-not-have");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SignInAsync(browser, "app1", Ct));

        TokenGrant refused = Assert.Single(harness.Idp.Grants);
        Assert.False(refused.Ok, refused.ToString());
    }

    [Fact]
    public async Task F09_a_removed_host_returns_404_and_its_sessions_are_gone()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        HarnessApp app2 = harness.App("app2");

        await harness.SignInAsync(browser, "app1", Ct);
        await harness.SignInAsync(browser, "app2", Ct);
        string app2Cookies = browser.Cookies.GetCookieHeader(app2.Origin);

        RefreshOutcome removed = await harness.UpdateRegistryAsync(document =>
            document["apps"]!.AsArray().Remove(Entry(document, "app2")), Ct);
        BrowserResponse gone = await browser.NavigateAsync(app2.Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        BrowserResponse neighbour = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);

        Assert.Equal(RefreshOutcome.Swapped, removed);
        Assert.Equal((HttpStatusCode)Contract.Error("not_found").Status, gone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, neighbour.StatusCode);
        Assert.Equal(FixtureFiles.Spa("app1")["index.html"], neighbour.Bytes);

        // App 1's session outlived app 2's removal: its shell came back with no trip to the provider.
        Assert.Empty(neighbour.RedirectedFrom);

        // Its sessions are gone, not just unreachable: the same app back under the same client does not
        // honour the cookie the browser held before the removal.
        RefreshOutcome back = await harness.UpdateRegistryAsync(document =>
            document["apps"]!.AsArray().Add(harness.EntryFor(app2, harness.Options.Release)), Ct);
        await browser.NavigateAsync(app2.Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        BrowserResponse replay = await browser.GetAsync("/bff/session", RequestOptions.With(("Cookie", app2Cookies)), Ct);

        Assert.Equal(RefreshOutcome.Swapped, back);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, replay.StatusCode);
    }

    private static JsonObject Entry(JsonObject document, string appId) =>
        document["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == appId)!.AsObject();
}
