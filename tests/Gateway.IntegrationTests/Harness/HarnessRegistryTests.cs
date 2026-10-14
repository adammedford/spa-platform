using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// The harness changes the registry while the gateway runs, and the change is live after one poll: the
/// mechanism F-07, F-09 and N-14 are built on. Each test starts its own harness, because each changes it.
/// </summary>
public sealed class HarnessRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RequestOptions NoRedirect = new() { Redirect = RedirectMode.Manual };

    [Fact]
    public async Task A_removed_app_stops_resolving_at_the_next_revision()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();

        RefreshOutcome outcome = await harness.UpdateRegistryAsync(document =>
        {
            JsonArray apps = document["apps"]!.AsArray();
            apps.Remove(apps.Single(app => app!["appId"]!.GetValue<string>() == "app2"));
        }, Ct);

        BrowserResponse removed = await browser.NavigateAsync(harness.App("app2").Url("/"), NoRedirect, Ct);
        BrowserResponse kept = await browser.NavigateAsync(harness.App("app1").Url("/"), NoRedirect, Ct);

        Assert.Equal(RefreshOutcome.Swapped, outcome);
        Assert.Equal(2, harness.Registry.Current.Revision);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Null(removed.Header("Content-Security-Policy"));
        Assert.NotNull(kept.Header("Content-Security-Policy"));
    }

    [Fact]
    public async Task An_app_onboarded_while_the_gateway_runs_resolves_with_its_own_client_and_no_restart()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        IServiceProvider before = harness.Services;

        (HarnessApp app3, RefreshOutcome outcome) = await harness.OnboardAsync("app3", Ct);
        BrowserResponse served = await browser.NavigateAsync(app3.Url("/"), NoRedirect, Ct);

        Assert.Equal(RefreshOutcome.Swapped, outcome);
        Assert.Same(before, harness.Services);
        Assert.Equal("app3.spa-poc.test:8443", app3.Host);
        Assert.Contains(app3.ClientId, harness.Idp.Clients.Keys);
        Assert.Equal(app3.ClientId, harness.Registry.Current.ByAppId["app3"].OAuth.ClientId);
        Assert.NotNull(served.Header("Content-Security-Policy"));

        // Admission is the API owner's, so the harness leaves it to the test (F-07).
        Assert.False(harness.MockApi.AdmissionOf("reports").IsAdmitted(app3.ClientId));
    }

    [Fact]
    public async Task A_document_that_is_not_valid_is_rejected_and_the_live_revision_stays()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);

        RefreshOutcome garbage = await harness.PublishRegistryTextAsync("{ not json", Ct);
        RefreshOutcome duplicate = await harness.UpdateRegistryAsync(document =>
        {
            JsonArray apps = document["apps"]!.AsArray();
            JsonObject copy = (JsonObject)apps[0]!.DeepClone();
            copy["appId"] = "app9";
            apps.Add(copy);
        }, Ct);

        Assert.Equal(RefreshOutcome.Rejected, garbage);
        Assert.Equal(RefreshOutcome.Rejected, duplicate);
        Assert.Equal(1, harness.Registry.Current.Revision);
    }
}
