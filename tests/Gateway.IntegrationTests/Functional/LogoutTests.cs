using System.Net;
using Gateway.TestHarness;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-04 (docs/plan/04-tests.md): log out, through the composed gateway with a session required. Task 6.2.
///
/// <para>The test provider approves every authorization at once, so a browser that followed every
/// redirect after logout would land on the shell signed in again and hide what the row is about. The
/// logout is therefore followed one redirect at a time, and stops where the browser reaches the
/// provider's authorization endpoint: that is the provider's login page.</para>
/// </summary>
public sealed class LogoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RequestOptions Manual = new() { Redirect = RedirectMode.Manual };

    [Fact]
    public async Task F04_logout_ends_the_session_clears_the_antiforgery_cookie_revokes_and_the_next_navigation_is_the_providers_login()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        HarnessApp app = harness.App("app1");

        await harness.SignInAsync(browser, "app1", Ct);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/bff/session", ct: Ct)).StatusCode);
        string signedIn = browser.Cookies.GetCookieHeader(app.Origin);
        TokenGrant login = Assert.Single(harness.Idp.Grants, grant => grant.GrantType == "authorization_code");
        Assert.Contains("__Host-spa-csrf=", signedIn, StringComparison.Ordinal);

        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: Manual, ct: Ct);

        // The antiforgery cookie is cleared, and so is the session cookie: the jar holds nothing live for the app.
        Assert.Contains(logout.SetCookies, line => line.StartsWith("__Host-spa-csrf=", StringComparison.Ordinal) && Expires(line));
        Assert.DoesNotContain(browser.Cookies.GetCookies(app.Origin), cookie => !cookie.Expired);

        // Through the provider's logout, back to the app.
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(harness.Idp.EndSessionEndpoint, new Uri(logout.Header("Location")!).GetLeftPart(UriPartial.Path));
        BrowserResponse back = await FollowAsync(browser, logout);
        Assert.Equal(1, harness.Idp.EndSessions);

        // The session is gone at the gateway: the cookie the browser held before logout is anonymous now.
        BrowserResponse replay = await browser.GetAsync("/bff/session", RequestOptions.With(("Cookie", signedIn)), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The provider saw the revocation of the session's refresh token, by app 1's own client.
        Revocation revocation = Assert.Single(harness.Idp.Revocations);
        Assert.Equal(app.ClientId, revocation.ClientId);
        Assert.Equal(login.RefreshTokenIssued, revocation.Token);
        Assert.True(revocation.Known && revocation.Revoked >= 1, revocation.ToString());
        Assert.False(harness.Idp.IsRedeemable(login.RefreshTokenIssued));

        // The next navigation to the app shows the provider's login page, not the shell.
        BrowserResponse next = back.Url.AbsoluteUri.StartsWith(harness.Idp.AuthorizeEndpoint, StringComparison.Ordinal)
            ? back
            : await FollowAsync(browser, await browser.NavigateAsync(app.Url("/"), Manual, Ct));
        Assert.StartsWith(harness.Idp.AuthorizeEndpoint + "?", next.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Single(harness.Idp.Authorizations);
    }

    /// <summary>
    /// Follows redirects one at a time, as navigations, until an answer is not a redirect or the next hop
    /// is the provider's authorization endpoint. Returns the last answer, or for the authorization
    /// endpoint an answer naming it that was not requested.
    /// </summary>
    private static async Task<BrowserResponse> FollowAsync(TestBrowser browser, BrowserResponse from)
    {
        BrowserResponse current = from;
        for (int hop = 0; hop < TestBrowser.MaxRedirects; hop++)
        {
            if ((int)current.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return current;
            }

            Uri next = new(current.Url, current.Header("Location")!);
            if (next.AbsolutePath == "/authorize")
            {
                return current with { Url = next };
            }

            current = await browser.ClickAsync(next.ToString(), Manual, Ct);
        }

        throw new InvalidOperationException("too many redirects after logout.");
    }

    private static bool Expires(string setCookie) =>
        setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
        || setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase);
}
