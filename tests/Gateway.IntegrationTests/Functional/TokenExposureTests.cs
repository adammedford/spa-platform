using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Gateway.TestHarness;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-05 (docs/plan/04-tests.md): after login, dump the cookies and every response the browser received,
/// and find two cookies and no token. Through the composed gateway with a session required. Task 6.2.
///
/// <para>Web storage is not dumped here: the test browser runs no script, so nothing can be in it. The
/// deployed tier dumps a real browser's storage.</para>
/// </summary>
public sealed partial class TokenExposureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A JWT: three base64url segments, the first starting <c>eyJ</c> (an encoded <c>{"</c>). The provider's
    /// access and ID tokens are JWTs, so one from any source is caught by its shape as well as by value.
    /// </summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [Fact]
    public async Task F05_after_login_and_api_calls_the_browser_holds_two_cookies_and_no_token()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(new GatewayHarnessOptions { SessionRequired = true }, Ct);
        using TestBrowser browser = harness.CreateBrowser();
        HarnessApp app = harness.App("app1");

        // A refresh during the visit, so the rotated tokens are among those searched for.
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        BrowserResponse session = await browser.GetAsync("/bff/session", ct: Ct);
        BrowserResponse whoami = await browser.GetAsync("/api/orders/whoami", ct: Ct);
        BrowserResponse echo = await browser.GetAsync("/api/orders/echo", ct: Ct);

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Single(harness.Idp.RefreshGrants);

        // Two cookies: the session and the antiforgery token. The provider's correlation and nonce
        // cookies were removed on the way back from the callback.
        Assert.Equal(
            new[] { "__Host-spa-csrf", SessionCookieName(browser, app) }.Order(StringComparer.Ordinal),
            browser.Cookies.GetCookies(app.Origin).Where(cookie => !cookie.Expired).Select(cookie => cookie.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(browser.AllCookies, cookie => !cookie.Expired && cookie.Domain != app.Origin.Host);

        // Every exchange the browser saw, with the provider's included: status line, headers and body,
        // and every cookie value it holds.
        string dump = Dump(browser);

        // The dump holds what the browser received, so the searches below can fail: the API's answer
        // and the session cookie's value are in it (CODING_STANDARDS.md -> "An assertion must be able to fail").
        Assert.Contains(whoami.Body, dump, StringComparison.Ordinal);
        string sessionValue = browser.Cookies.GetCookies(app.Origin)[SessionCookieName(browser, app)]!.Value;
        Assert.True(dump.Contains(sessionValue, StringComparison.Ordinal), "the session cookie's value is not in the dump");
        Assert.NotEmpty(harness.Idp.IssuedAccessTokens);
        Assert.NotEmpty(harness.Idp.IssuedRefreshTokens);
        foreach (string token in harness.Idp.IssuedAccessTokens.Concat(harness.Idp.IssuedRefreshTokens))
        {
            // Named by label, never by value, so a failure does not print the token.
            Assert.False(dump.Contains(token, StringComparison.Ordinal), $"found {TestIdentityProvider.Label(token)}");
        }

        Assert.False(Jwt().IsMatch(dump), "found a JWT-shaped string in what the browser received");
    }

    [Fact]
    public void F05_control_the_search_finds_a_token_where_one_is()
    {
        // The shape the search looks for, in the form the provider issues it, so a search that could
        // never match would fail here and not pass F-05 by default.
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2ln";
        Assert.Matches(Jwt(), "{\"name\":\"" + jwt + "\"}");
    }

    /// <summary>The app's other cookie, whichever name the session cookie has; there must be exactly one.</summary>
    private static string SessionCookieName(TestBrowser browser, HarnessApp app) =>
        Assert.Single(browser.Cookies.GetCookies(app.Origin), cookie => !cookie.Expired && cookie.Name != "__Host-spa-csrf").Name;

    private static string Dump(TestBrowser browser)
    {
        StringBuilder text = new();
        foreach (BrowserResponse exchange in browser.Exchanges)
        {
            text.Append(exchange.Url).Append(' ').Append((int)exchange.StatusCode).AppendLine();
            foreach ((string name, System.Collections.Immutable.ImmutableArray<string> values) in exchange.Headers)
            {
                text.Append(name).Append(": ").AppendJoin(", ", values).AppendLine();
            }

            text.AppendLine(exchange.Body);
        }

        foreach (System.Net.Cookie cookie in browser.AllCookies)
        {
            text.Append(cookie.Name).Append('=').AppendLine(cookie.Value);
        }

        return text.ToString();
    }
}
