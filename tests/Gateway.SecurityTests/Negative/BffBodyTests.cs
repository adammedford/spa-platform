using System.Net;
using System.Text.RegularExpressions;
using Gateway.TestHarness;
using TestIdp;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-23: "Inspect every <c>/bff</c> response body -> No token and no token-shaped string."
/// (docs/plan/04-tests.md.)
///
/// <para>Every <c>/bff</c> endpoint is driven through a whole visit on a cell that requires a session:
/// login (its redirect), session reads before and after a refresh, logout, and the session read that
/// fails after it. Every exchange whose path starts <c>/bff</c> is then searched for each token the
/// provider issued during the run, by value, and for anything shaped like a JWT. Workstream 3 holds
/// the endpoints to this in its own suite (task 3.9); here they are the composed gateway's.</para>
/// </summary>
public sealed partial class BffBodyTests
{
    /// <summary>A JWT: three base64url segments, the first an encoded <c>{"</c>.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [Fact]
    public async Task N23_no_bff_response_body_carries_a_token_or_a_token_shaped_string()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = harness.CreateBrowser();

        // An expired first access token, so the session read after it refreshes and the rotated
        // tokens are among those searched for.
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        BrowserResponse session = await SignedIn.SessionAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);
        Assert.NotEmpty(harness.Idp.RefreshGrants);

        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: SignedIn.Manual, ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        await SignedIn.OnPageOfAsync(browser, harness.App("app1"));
        BrowserResponse ended = await SignedIn.SessionAsync(browser);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, ended.StatusCode);

        BrowserResponse[] bff = [.. browser.Exchanges.Where(exchange => exchange.Url.AbsolutePath.StartsWith("/bff/", StringComparison.Ordinal))];

        // Every endpoint was read, and the bodies searched are real ones: the session body carries the
        // identity and the CSRF token, so a search over empty strings cannot pass for this one.
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/login");
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/logout");
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/session" && exchange.StatusCode == HttpStatusCode.OK);
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/session" && (int)exchange.StatusCode == Contract.Error("unauthenticated").Status);
        Assert.Contains(SignedIn.Property(session, "csrfToken")!, session.Body, StringComparison.Ordinal);

        string[] tokens = [.. harness.Idp.IssuedAccessTokens, .. harness.Idp.IssuedRefreshTokens];
        Assert.True(harness.Idp.IssuedAccessTokens.Count >= 2, "the visit refreshed, so at least two access tokens were issued");
        foreach (BrowserResponse exchange in bff)
        {
            foreach (string token in tokens)
            {
                // Named by label, never by value, so a failure does not print the token.
                Assert.False(exchange.Body.Contains(token, StringComparison.Ordinal), $"{exchange} carries {TestIdentityProvider.Label(token)}");
            }

            Assert.False(Jwt().IsMatch(exchange.Body), $"{exchange} carries a JWT-shaped string");
        }
    }

    [Fact]
    public void N23_control_the_shape_search_finds_a_jwt_where_one_is()
    {
        Assert.Matches(Jwt(), "{\"accessToken\":\"eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2ln\"}");
    }
}
