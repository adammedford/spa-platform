using System.Text;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-05 in a real browser (docs/plan/04-tests.md, tier D): after login, dump cookies, web storage and
/// every response body, and find two cookies and no token. Web storage is what the hermetic tier could
/// not dump, because its browser runs no script; here the fixture shell's script has run. On the
/// local stack a refresh happens during the visit and every token value the provider issued is
/// searched for too ("In H"); on the cell the search is by shape.
/// </summary>
public sealed class ExposureTests
{
    [Fact]
    public async Task F05_after_login_and_api_calls_the_browser_holds_two_cookies_no_storage_and_no_token()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        LocalStack? local = stack as LocalStack;

        // Locally, a refresh during the visit, so the rotated tokens are among those searched for.
        if (local is not null)
        {
            await local.SignInWithExpiredAccessTokenAsync(profile, app);
        }
        else
        {
            await profile.SignInAsync(app);
        }

        await profile.Page.ClickAsync("#call-api");
        await profile.Page.ClickAsync("#post-api");
        await Assertions.Expect(profile.Page.Locator("#api-result")).ToContainTextAsync("200 ");
        await Assertions.Expect(profile.Page.Locator("#post-result")).ToHaveTextAsync("200");
        if (local is not null)
        {
            Assert.Single(local.Harness.Idp.RefreshGrants);
        }

        // Two cookies in the whole browser, both the app's own: the session and the antiforgery token. The
        // login-state cookies went at the callback. The provider's own cookies, on its own domain, are
        // its session with the user, not the platform's.
        IReadOnlyList<BrowserContextCookiesResult> cookies =
            [.. (await profile.Context.CookiesAsync()).Where(cookie => cookie.Domain.TrimStart('.') != stack.ProviderHost)];
        Assert.Equal(
            new[] { Contract.PlatformCookie("-csrf"), Contract.PlatformCookie("-session") }.Order(StringComparer.Ordinal),
            cookies.Select(cookie => cookie.Name).Order(StringComparer.Ordinal));
        Assert.All(cookies, cookie => Assert.Equal(app.Origin.Host, cookie.Domain));

        // Web storage, as the page's own script sees it.
        string storage = await profile.Page.EvaluateAsync<string>(WebStorage.Script);
        WebStorage.AssertEmpty(storage);

        // Every response the browser received from the cell, with its headers, every cookie value, the
        // storage and the page as it stands.
        StringBuilder dump = new();
        foreach (Exchange exchange in await stack.ExchangesAsync())
        {
            // The whole URL, query included, written out: Exchange.ToString prints none (Redacted), and a
            // token in a URL is one the browser holds.
            dump.Append(exchange.Method).Append(' ').Append(exchange.Url).Append(" -> ").Append(exchange.Status).AppendLine();
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
        // the session cookie's value are in it. No message prints the dump, the cookie or a token found in
        // it: on the cell they are real (AGENTS.md).
        Assert.True(text.Contains(app.ClientId, StringComparison.Ordinal), "the dump does not hold the API's answer, so the searches below could not fail.");
        Assert.True(
            text.Contains(cookies.First(cookie => cookie.Name == Contract.PlatformCookie("-session")).Value, StringComparison.Ordinal),
            "the dump does not hold the session cookie's value, so the searches below could not fail.");

        TokenShape.AssertNoJwtIn(text, "what the browser holds");
        if (local is not null)
        {
            TokenShape.AssertNoneIssuedIn(local.Harness.Idp, text, "what the browser holds");
        }
    }
}
