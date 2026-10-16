using System.Net;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-01 and F-02 (docs/plan/04-tests.md), through the composed gateway with a session required, as it
/// ships: one gateway process serving two apps, each signing in with its own client. Task 6.2.
/// </summary>
public sealed class SignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GatewayHarnessOptions Options = new() { SessionRequired = true };

    [Fact]
    public async Task F01_signed_in_on_two_hosts_one_gateway_serves_two_different_shells()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Ct);
        await harness.SignInAsync(browser, "app2", Ct);
        BrowserResponse one = await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse two = await browser.NavigateAsync(harness.App("app2").Url("/"), ct: Ct);

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);

        // Each host's own shell, from its own release prefix, and the two are not the same document.
        Assert.Equal(FixtureFiles.Spa("app1")["index.html"], one.Bytes);
        Assert.Equal(FixtureFiles.Spa("app2")["index.html"], two.Bytes);
        Assert.NotEqual(one.Bytes, two.Bytes);

        // Neither navigation was sent anywhere else first: each was answered by the gateway on its own host.
        Assert.Empty(one.RedirectedFrom);
        Assert.Empty(two.RedirectedFrom);

        // One process answered both: the harness has one gateway address behind one stand-in load
        // balancer, the in-process equivalent of one task behind one ALB, and one registry names both apps.
        Assert.Equal(2, harness.Registry.Current.ByAppId.Count);
    }

    [Fact]
    public async Task F01_control_without_a_session_the_shell_is_not_served()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse challenged = await browser.NavigateAsync(
            harness.App("app1").Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);

        // The shell gate (ADR-0008): a navigation without a session goes to the provider, and the
        // shell's bytes are not in the answer. Without this, F-01's 200s would hold with no gate at all.
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        Assert.StartsWith(harness.Idp.AuthorizeEndpoint + "?", challenged.Header("Location"), StringComparison.Ordinal);
        Assert.NotEqual(FixtureFiles.Spa("app1")["index.html"], challenged.Bytes);
    }

    [Fact]
    public async Task F02_each_app_logs_in_with_its_own_client_and_returns_to_its_own_callback()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        foreach (string appId in (string[])["app1", "app2"])
        {
            HarnessApp app = harness.App(appId);
            BrowserResponse landed = await harness.SignInAsync(browser, appId, Ct);

            // The redirect chain the browser followed: the app's login, the provider, the app's callback.
            Uri authorize = Assert.Single(landed.RedirectedFrom, hop => hop.AbsoluteUri.StartsWith(harness.Idp.AuthorizeEndpoint, StringComparison.Ordinal));
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(authorize.Query);
            Assert.Equal(app.ClientId, query["client_id"].ToString());
            Assert.Equal(app.Url("/signin-oidc").ToString(), query["redirect_uri"].ToString());
            Assert.Contains(landed.RedirectedFrom, hop => hop.GetLeftPart(UriPartial.Path) == app.Url("/signin-oidc").ToString());
            Assert.Equal(app.Url("/"), landed.Url);
        }

        // What the provider saw: one authorization per client, each to its own callback, and each code
        // redeemed by the client it was issued to.
        Assert.Collection(harness.Idp.Authorizations,
            first => Assert.Equal((harness.App("app1").ClientId, harness.App("app1").Url("/signin-oidc").ToString(), "ok"), (first.ClientId, first.RedirectUri, first.Outcome)),
            second => Assert.Equal((harness.App("app2").ClientId, harness.App("app2").Url("/signin-oidc").ToString(), "ok"), (second.ClientId, second.RedirectUri, second.Outcome)));
        Assert.Equal(
            [harness.App("app1").ClientId, harness.App("app2").ClientId],
            harness.Idp.Grants.Where(grant => grant.GrantType == "authorization_code" && grant.Ok).Select(grant => grant.ClientId));
    }

    [Fact]
    public async Task F02_a_session_on_one_app_is_not_a_session_on_the_other()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        await harness.SignInAsync(browser, "app1", Ct);
        BrowserResponse own = await browser.GetAsync("/bff/session", ct: Ct);

        // On app 2's page without following its challenge, which would sign in to app 2 at once.
        await browser.NavigateAsync(harness.App("app2").Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        BrowserResponse other = await browser.GetAsync("/bff/session", ct: Ct);

        // Signing in to app 1 did not sign the user in to app 2: each app has its own client and its own
        // session, so app 2 needs its own login.
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Single(harness.Idp.Authorizations);
        Assert.Equal(harness.Idp.DefaultSubject, System.Text.Json.JsonDocument.Parse(own.Body).RootElement.GetProperty("sub").GetString());
    }
}
