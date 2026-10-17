using System.Net;
using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-08 (docs/plan/04-tests.md): call each mock API route through the gateway, from both apps. The mock
/// API is the only observer that can tell app 1's token from app 2's, so what it reports is what this
/// row asserts. Through the composed gateway with a session required. Task 6.2.
/// </summary>
public sealed class MockApiRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each app and the listener that admits its client (the harness's mock API admits app 1 on orders and
    /// app 2 on reports, as the cell does).
    /// </summary>
    public static TheoryData<string, string> OwnListener => new()
    {
        { "app1", "orders" },
        { "app2", "reports" },
    };

    [Theory]
    [MemberData(nameof(OwnListener))]
    public async Task F08_whoami_reports_the_apps_own_client_scopes_and_bound_audience(string appId, string api)
    {
        // What the app asked for, from its definition (fixtures/apps): the read scope of the API it calls,
        // and the resource it binds its token to, which app 1 names and app 2 does not.
        System.Text.Json.Nodes.JsonObject oauth = FixtureFiles.AppDefinition(appId)["oauth"]!.AsObject();
        string scope = Assert.Single(oauth["scopes"]!.AsArray().Select(node => node!.GetValue<string>()),
            candidate => candidate.EndsWith("/read", StringComparison.Ordinal) && candidate.Contains(api, StringComparison.Ordinal));
        string? audience = oauth["resource"]?.GetValue<string>();

        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, appId, Ct);

        BrowserResponse whoami = await browser.GetAsync($"/api/{api}/whoami", ct: Ct);

        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        using JsonDocument body = JsonDocument.Parse(whoami.Body);
        JsonElement root = body.RootElement;
        Assert.Equal(api, root.GetProperty("api").GetString());
        Assert.Equal(harness.App(appId).ClientId, root.GetProperty("clientId").GetString());
        Assert.Equal(harness.Idp.DefaultSubject, root.GetProperty("subject").GetString());
        Assert.Contains(scope, root.GetProperty("scopes").EnumerateArray().Select(element => element.GetString()));
        Assert.Equal(audience, root.GetProperty("audience").GetString());
    }

    [Theory]
    [MemberData(nameof(OwnListener))]
    public async Task F08_admin_returns_403_for_want_of_scope(string appId, string api)
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, appId, Ct);

        BrowserResponse admin = await browser.GetAsync($"/api/{api}/admin", ct: Ct);

        // The listener admits this client, so the 403 is about the admin scope no app asks for, not
        // about who is calling.
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        using JsonDocument body = JsonDocument.Parse(admin.Body);
        Assert.Equal("insufficient_scope", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task F08_each_app_calls_with_its_own_token_so_the_other_apps_listener_refuses_it()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Ct);

        // Both apps list both APIs. App 1's token reaches reports, which has not admitted app 1's client:
        // if the gateway sent one token for every app, this would succeed.
        BrowserResponse reports = await browser.GetAsync("/api/reports/whoami", ct: Ct);

        Assert.Equal(HttpStatusCode.Forbidden, reports.StatusCode);
        using JsonDocument body = JsonDocument.Parse(reports.Body);
        Assert.Equal("client_not_admitted", body.RootElement.GetProperty("error").GetString());
    }
}
