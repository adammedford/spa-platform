using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-08 in a real browser (docs/plan/04-tests.md, tier D), on the stack <see cref="StackSwitch"/>
/// chooses: an unknown host and an app host on the wrong port, typed into the address bar, and a
/// spoofed <c>X-Forwarded-Host</c>, which a page's own script can send because it is not a forbidden
/// header name (https://fetch.spec.whatwg.org/#forbidden-request-header).
/// </summary>
public sealed class HostTests
{
    [Fact]
    public async Task N08_an_unknown_host_and_an_app_host_on_the_wrong_port_are_404()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        List<Uri> navigations = profile.RecordNavigations();

        LogMark mark = stack.MarkLog();
        IResponse? unknown = await profile.GotoAsync($"https://unknown.{stack.ParentDomain}:{app.Origin.Port}/");
        Assert.Equal(Contract.Error("not_found").Status, unknown?.Status);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);

        // The resolver rule sends every port to the same listener, so only the Host header differs.
        mark = stack.MarkLog();
        IResponse? wrongPort = await profile.GotoAsync(new UriBuilder(app.Origin) { Port = app.Origin.Port + 1000 }.Uri.ToString());
        Assert.Equal(Contract.Error("not_found").Status, wrongPort?.Status);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);

        // Neither started a login: an unknown host has no app to sign in to.
        // A failure prints the navigations only as Redacted does: an authorize request carries the provider's state.
        Assert.False(navigations.Any(url => url.GetLeftPart(UriPartial.Path) == stack.AuthorizeEndpoint), Redacted.Urls(navigations));
        if (stack is LocalStack local)
        {
            Assert.Empty(local.Harness.Idp.Authorizations); // In H the provider saw no authorization either.
        }
    }

    [Fact]
    public async Task N08_a_spoofed_forwarded_host_changes_nothing()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
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
        Exchange sent = (await stack.ExchangesAsync()).Last(exchange => exchange.Url.AbsolutePath == "/bff/session");
        Assert.Equal(app2.Host, sent.Sent("X-Forwarded-Host"));

        // And from an unknown host, naming app 1: still unknown.
        using HttpRequestMessage unknown = new(HttpMethod.Get, $"https://unknown.{stack.ParentDomain}:{app1.Origin.Port}/");
        unknown.Headers.Add("X-Forwarded-Host", app1.Host);
        LogMark mark = stack.MarkLog();
        using HttpResponseMessage answer = await stack.SendRawAsync(unknown);
        Assert.Equal(Contract.Error("not_found").Status, (int)answer.StatusCode);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);
    }
}
