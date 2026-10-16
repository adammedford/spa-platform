using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-08 in a real browser (docs/plan/04-tests.md, tier D): an unknown host and an app host on the
/// wrong port, typed into the address bar, and a spoofed <c>X-Forwarded-Host</c>, which a page's own
/// script can send because it is not a forbidden header name
/// (https://fetch.spec.whatwg.org/#forbidden-request-header).
/// </summary>
public sealed class HostTests
{
    [Fact]
    public async Task N08_an_unknown_host_and_an_app_host_on_the_wrong_port_are_404()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        int mark = stack.RequestLines.Count;
        IResponse? unknown = await profile.Page.GotoAsync($"https://unknown.{LocalStack.Domain}:{app.Origin.Port}/");
        Assert.Equal(404, unknown?.Status);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);

        mark = stack.RequestLines.Count;
        IResponse? wrongPort = await profile.Page.GotoAsync(new UriBuilder(app.Origin) { Port = app.Origin.Port + 1000 }.Uri.ToString());
        Assert.Equal(404, wrongPort?.Status);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);

        // Neither started a login: an unknown host has no app to sign in to.
        Assert.Empty(stack.Harness.Idp.Authorizations);
    }

    [Fact]
    public async Task N08_a_spoofed_forwarded_host_changes_nothing()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");
        await profile.SignInAsync(app1);

        // From app 1's page, naming app 2 in X-Forwarded-Host: still app 1's session and app 1's client.
        Dictionary<string, string> spoofed = new() { ["X-Forwarded-Host"] = app2.Host };
        FetchResult whoami = await profile.FetchAsync("/api/orders/whoami", headers: spoofed);
        Assert.Equal(200, whoami.Status);
        using (JsonDocument body = JsonDocument.Parse(whoami.Body))
        {
            Assert.Equal(app1.ClientId, body.RootElement.GetProperty("clientId").GetString());
        }

        Assert.Equal(200, (await profile.FetchAsync("/bff/session", headers: spoofed)).Status);
        Exchange sent = stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url.AbsolutePath == "/bff/session");
        Assert.Equal(app2.Host, sent.Sent("X-Forwarded-Host"));

        // And from an unknown host, naming app 1: still unknown.
        using HttpRequestMessage unknown = new(HttpMethod.Get, $"https://unknown.{LocalStack.Domain}:{app1.Origin.Port}/");
        unknown.Headers.Add("X-Forwarded-Host", app1.Host);
        using HttpResponseMessage answer = await stack.SendRawAsync(unknown);
        Assert.Equal(404, (int)answer.StatusCode);
    }
}
