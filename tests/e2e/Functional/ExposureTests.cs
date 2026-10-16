using System.Text;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-05 in a real browser (docs/plan/04-tests.md, tier D): after login, dump cookies, web storage and
/// every response body, and find two cookies and no token. Web storage is what the hermetic tier could
/// not dump, because its browser runs no script; here the fixture shell's script has run.
/// </summary>
public sealed class ExposureTests
{
    [Fact]
    public async Task F05_after_login_and_api_calls_the_browser_holds_two_cookies_no_storage_and_no_token()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        // A refresh during the visit, so the rotated tokens are among those searched for.
        stack.Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await profile.SignInAsync(app);
        stack.Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        await profile.Page.ClickAsync("#call-api");
        await profile.Page.ClickAsync("#post-api");
        await Assertions.Expect(profile.Page.Locator("#api-result")).ToContainTextAsync("200 ");
        await Assertions.Expect(profile.Page.Locator("#post-result")).ToHaveTextAsync("200");
        Assert.Single(stack.Harness.Idp.RefreshGrants);

        // Two cookies in the whole browser, both the app's own: the session and the antiforgery token. The
        // login-state cookies went at the callback.
        IReadOnlyList<BrowserContextCookiesResult> cookies = await profile.Context.CookiesAsync();
        Assert.Equal(
            new[] { Contract.PlatformCookie("-csrf"), Contract.PlatformCookie("-session") }.Order(StringComparer.Ordinal),
            cookies.Select(cookie => cookie.Name).Order(StringComparer.Ordinal));
        Assert.All(cookies, cookie => Assert.Equal(app.Origin.Host, cookie.Domain));

        // Web storage, as the page's own script sees it.
        string storage = await profile.Page.EvaluateAsync<string>(
            "() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } })");
        Assert.Equal("""{"local":{},"session":{}}""", storage);

        // Every response the browser received from the cell, with its headers, every cookie value, the
        // storage and the page as it stands.
        StringBuilder dump = new();
        foreach (Exchange exchange in stack.LoadBalancer.Exchanges)
        {
            dump.AppendLine(exchange.ToString());
            foreach ((string name, string value) in exchange.Headers)
            {
                dump.Append(name).Append(": ").AppendLine(value);
            }

            dump.AppendLine(exchange.BodyText);
        }

        foreach (BrowserContextCookiesResult cookie in cookies)
        {
            dump.Append(cookie.Name).Append('=').AppendLine(cookie.Value);
        }

        dump.AppendLine(storage).AppendLine(await profile.Page.ContentAsync());
        string text = dump.ToString();

        // The dump holds what the browser received, so the searches below can fail: the API's answer and
        // the session cookie's value are in it.
        Assert.Contains(app.ClientId, text, StringComparison.Ordinal);
        Assert.Contains(cookies.First(cookie => cookie.Name == Contract.PlatformCookie("-session")).Value, text, StringComparison.Ordinal);

        Assert.DoesNotMatch(TokenShape.Jwt(), text);
        Assert.NotEmpty(TokenShape.IssuedBy(stack.Harness.Idp));
        Assert.All(TokenShape.IssuedBy(stack.Harness.Idp), token => Assert.DoesNotContain(token, text, StringComparison.Ordinal));
    }
}
