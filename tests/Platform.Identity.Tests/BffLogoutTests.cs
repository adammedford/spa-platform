using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>POST /bff/logout</c> (task 3.9, contracts/http-contract.md -> Endpoints, ADR-0017 decision 4):
/// attempts to revoke the session's refresh token, always removes the session, clears the antiforgery
/// cookie, then redirects through the provider's logout. F-04 and S-E4, N-23 for this endpoint, and
/// F-05 over a whole signed-in visit. Where each profile then sends the browser is in
/// <see cref="LogoutTests"/>.
/// </summary>
public sealed class BffLogoutTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task Logout_revokes_the_refresh_token_removes_the_session_clears_both_cookies_and_leaves_through_the_provider()
    {
        // F-04.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        string refreshToken = Assert.Single(gateway.Idp.IssuedRefreshTokens);

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        // The provider saw the revocation, from this app's client, of the token the session held.
        Revocation revocation = Assert.Single(gateway.Idp.Revocations);
        Assert.Equal(App1.OAuth.ClientId, revocation.ClientId);
        Assert.Equal(refreshToken, revocation.Token);
        Assert.True(revocation.Known);
        Assert.False(gateway.Idp.IsRedeemable(refreshToken));

        // The session is gone, and both cookies the browser keeps are expired on the response.
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.True(Expired(logout, login.Callback.SessionCookie.Name.Value!));
        Assert.True(Expired(logout, FakeCsrfTokens.CookieName));

        // Then the provider's logout, which comes back to the app.
        Assert.Equal(302, logout.Status);
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));

        // The next navigation is challenged: the provider's login page.
        IdentityGateway.Answer next = await gateway.SendAsync(App1, "/", headers: IdentityGateway.Navigation);
        Assert.Equal(302, next.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, next.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaying_the_old_still_decryptable_cookie_after_logout_is_anonymous()
    {
        // S-E4.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);
        IdentityGateway.Answer shell = await gateway.SendAsync(App1, "/", login.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(401, session.Status);
        Assert.Null(session.Subject);
        Assert.Equal(RuleIds.AuthSession, session.Rule);
        Assert.Equal(302, shell.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, shell.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token_the_session_holds_now_after_a_refresh_rotated_it()
    {
        // The record is read under the session's gate, not the ticket this request authenticated with.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        gateway.Idp.Faults.AccessTokenLifetime = TimeSpan.FromSeconds(30);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        IdentityGateway.Answer token = await gateway.SendAsync(App1, IdentityGateway.TokenPath, login.Jar, headers: IdentityGateway.Fetch);
        Assert.Equal(200, token.Status);
        Assert.Equal(2, gateway.Idp.IssuedRefreshTokens.Count);

        await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        Assert.Equal(gateway.Idp.IssuedRefreshTokens[^1], Assert.Single(gateway.Idp.Revocations).Token);
    }

    [Theory]
    [InlineData(ClientCredentialStyle.PostBody, ClientAuth.Post)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader, ClientAuth.Basic)]
    public async Task The_revocation_presents_the_secret_in_the_style_the_issuers_profile_states(
        ClientCredentialStyle style, ClientAuth expected)
    {
        // ADR-0017 decision 2: Cognito's revocation endpoint takes only the Basic header (C-2.19).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        gateway.Profile.ClientCredentialStyle = style;
        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        Assert.Equal(expected, Assert.Single(gateway.Idp.Revocations).Auth);
    }

    [Fact]
    public async Task A_failed_revocation_still_removes_the_session_and_still_logs_out()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);
        gateway.Idp.Faults.RevokeFailureStatus = 503;
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        Assert.True(gateway.Idp.IsRedeemable(Assert.Single(gateway.Idp.IssuedRefreshTokens)));
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(302, logout.Status);
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
    }

    [Theory]
    [InlineData(null, "revoked")]
    [InlineData(503, "http_503")]
    public async Task The_providers_answer_to_the_revocation_is_logged_and_the_token_is_not(int? failure, string outcome)
    {
        // ADR-0017 decision 4: the response is checked, and the token reaches no log line (N-16).
        CapturingLoggerProvider logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton<ILoggerProvider>(logs), stubProfile: false);
        gateway.Idp.Faults.RevokeFailureStatus = failure;
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        string line = Assert.Single(logs.Lines, l => l.Category == typeof(SessionEnd).FullName).Message;
        Assert.EndsWith(": " + outcome + ".", line, StringComparison.Ordinal);
        Assert.Contains(SessionRef.Of(key), line, StringComparison.Ordinal);
        string all = string.Join("\n", logs.Lines.Select(l => l.Message));
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
        TokenSearch.AssertNoTokens(gateway.Idp, all);
    }

    [Fact]
    public async Task A_provider_that_advertises_no_revocation_endpoint_still_gets_the_session_removed()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);
        gateway.Idp.Faults.AdvertiseRevocation = false;
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");

        Assert.Empty(gateway.Idp.Revocations);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(302, logout.Status);
    }

    [Fact]
    public async Task Logout_without_a_session_revokes_nothing_and_still_clears_the_cookie_and_leaves_through_the_provider()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, "/bff/logout", method: "POST");

        Assert.Empty(gateway.Idp.Revocations);
        Assert.True(Expired(logout, FakeCsrfTokens.CookieName));
        Assert.Equal(302, logout.Status);
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Logging_out_of_one_app_leaves_the_other_apps_session()
    {
        // ADR-0017 decision 3: the gateway's logout is per app, whatever the provider's is.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login one = await gateway.LoginAsync(App1);
        IdentityGateway.Login two = await gateway.LoginAsync(App2);

        await gateway.SendAsync(App1, "/bff/logout", one.Jar, "POST");

        IdentityGateway.Answer session = await gateway.SendAsync(App2, "/bff/session", two.Jar, headers: IdentityGateway.Fetch);
        Assert.Equal(200, session.Status);
        Revocation revocation = Assert.Single(gateway.Idp.Revocations);
        Assert.Equal(App1.OAuth.ClientId, revocation.ClientId);
    }

    [Fact]
    public async Task A_signed_in_visit_leaves_two_cookies_and_no_token_in_any_body()
    {
        // F-05 and N-23: login, /bff/session, logout. What the browser holds after login is the
        // session cookie and the antiforgery cookie, and no body it received carries a token.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);
        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=/");
        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);

        Dictionary<string, string> jar = new(StringComparer.Ordinal);
        foreach (SetCookieHeaderValue cookie in login.Challenge.Cookies.Concat(login.Callback.Cookies).Concat(session.Cookies))
        {
            if (cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow)
            {
                jar.Remove(cookie.Name.Value!);
            }
            else
            {
                jar[cookie.Name.Value!] = cookie.Value.Value!;
            }
        }

        Assert.Equal(
            [FakeCsrfTokens.CookieName, login.Callback.SessionCookie.Name.Value!],
            jar.Keys.Order(StringComparer.Ordinal));
        Assert.All(jar.Values, value => TokenSearch.AssertNoTokens(gateway.Idp, value));

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, "/bff/logout", login.Jar, "POST");
        TokenSearch.AssertNoTokens(gateway.Idp, string.Concat(
            login.Challenge.Body, login.Callback.Body, session.Body, logout.Body));
    }

    private static bool Expired(IdentityGateway.Answer answer, string name) =>
        answer.Cookies.Any(c => c.Name == name && c.Expires is { } expires && expires < DateTimeOffset.UtcNow);
}
