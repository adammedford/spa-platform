using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Registry;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-07, hermetic tier (docs/plan/04-tests.md): onboard app 3 while apps 1 and 2 are in use. The row
/// is accepted at G5 with its deployed half (task id, start time, task definition and the plans); this
/// is the half the hermetic tier can show. Task 6.2.
/// </summary>
public sealed class OnboardingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task F07_app3_is_served_and_logs_in_with_its_own_client_with_no_restart_and_the_api_refuses_it_until_admitted()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser one = harness.CreateBrowser();
        using TestBrowser two = harness.CreateBrowser();
        using TestBrowser three = harness.CreateBrowser();
        IServiceProvider services = harness.Services;
        IServer server = harness.Services.GetRequiredService<IServer>();

        // Apps 1 and 2 in use: signed in and calling their APIs.
        await harness.SignInAsync(one, "app1", Ct);
        await harness.SignInAsync(two, "app2", Ct);
        Assert.Equal(HttpStatusCode.OK, (await one.GetAsync("/api/orders/whoami", ct: Ct)).StatusCode);

        (HarnessApp app3, RefreshOutcome outcome) = await harness.OnboardAsync("app3", Ct);
        BrowserResponse landed = await harness.SignInAsync(three, "app3", Ct);
        BrowserResponse refused = await three.GetAsync("/api/reports/whoami", ct: Ct);
        harness.MockApi.AdmissionOf("reports").Admit(app3.ClientId);
        BrowserResponse admitted = await three.GetAsync("/api/reports/whoami", ct: Ct);

        Assert.Equal(RefreshOutcome.Swapped, outcome);

        // Served: its own shell, after a login with its own client to its own callback.
        Assert.Equal(FixtureFiles.Spa("app3")["index.html"], landed.Bytes);
        Uri authorize = Assert.Single(landed.RedirectedFrom, hop => hop.AbsoluteUri.StartsWith(harness.Idp.AuthorizeEndpoint, StringComparison.Ordinal));
        Assert.Equal(app3.ClientId, QueryHelpers.ParseQuery(authorize.Query)["client_id"].ToString());
        Assert.Equal(app3.Url("/signin-oidc").ToString(), QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());

        // The API owner has not admitted app 3's client: the gateway sent a good token and the API said no.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (JsonDocument body = JsonDocument.Parse(refused.Body))
        {
            Assert.Equal("client_not_admitted", body.RootElement.GetProperty("error").GetString());
        }

        // In the hermetic tier the harness then admits client 3, and the same call succeeds as app 3.
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        using (JsonDocument body = JsonDocument.Parse(admitted.Body))
        {
            Assert.Equal(app3.ClientId, body.RootElement.GetProperty("clientId").GetString());
        }

        // No restart: the same process and listener, and apps 1 and 2 kept their sessions throughout.
        Assert.Same(services, harness.Services);
        Assert.Same(server, harness.Services.GetRequiredService<IServer>());
        BrowserResponse stillOne = await one.GetAsync("/api/orders/whoami", ct: Ct);
        BrowserResponse stillTwo = await two.GetAsync("/api/reports/whoami", ct: Ct);
        Assert.Equal(HttpStatusCode.OK, stillOne.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillTwo.StatusCode);
        Assert.Equal(3, harness.Idp.Authorizations.Count);
    }
}
