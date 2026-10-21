using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Functional;

/// <summary>The API token source preserves the dead-cookie rejection rule (contracts/telemetry.md).</summary>
public sealed class ApiSessionTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_present_but_invalid_session_cookie_marks_the_api_refusal(bool removedSession)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: ct);
        using TestBrowser browser = harness.CreateBrowser();
        HarnessApp app = harness.App("app1");
        string cookie = "__Host-spa-session=invalid";
        if (removedSession)
        {
            await harness.SignInAsync(browser, "app1", ct);
            cookie = browser.Cookies.GetCookieHeader(app.Origin);
            await harness.Services.GetRequiredService<ISessionStore>().RemoveAppAsync("app1", ct);
        }

        await browser.NavigateAsync(app.Url("/healthz"), ct: ct);
        BrowserResponse response = await browser.GetAsync(app.Url("/api/echo/items").ToString(),
            RequestOptions.With(("Cookie", cookie)), ct);

        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, response.StatusCode);
        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Any(line => line.GetProperty("route").GetString() == "api"),
            "the API refusal's request log line", ct);
        JsonElement line = Assert.Single(CapturedTelemetry.RequestLines(harness),
            line => line.GetProperty("route").GetString() == "api");
        Assert.Equal(RuleIds.AuthSession, line.GetProperty("rule").GetString());
        Assert.Empty(harness.Echo.Requests);
    }

    [Fact]
    public async Task An_api_request_without_a_session_cookie_is_not_a_rejection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: ct);
        using TestBrowser browser = harness.CreateBrowser();

        await browser.NavigateAsync(harness.App("app1").Url("/healthz"), ct: ct);
        BrowserResponse response = await browser.GetAsync("/api/echo/items", ct: ct);

        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, response.StatusCode);
        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Any(line => line.GetProperty("route").GetString() == "api"),
            "the API response's request log line", ct);
        JsonElement line = Assert.Single(CapturedTelemetry.RequestLines(harness),
            line => line.GetProperty("route").GetString() == "api");
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rule").ValueKind);
        Assert.Empty(harness.Echo.Requests);
    }
}
