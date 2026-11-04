using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>GET /bff/login?returnUrl=/path</c> (task 3.9, contracts/http-contract.md -> Endpoints): discards
/// any session, then redirects to the provider; a return path that is not local becomes <c>/</c>.
/// F-02 and N-15, and N-23 for this endpoint.
/// </summary>
public sealed class BffLoginTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Theory]
    [InlineData("app1")]
    [InlineData("app2")]
    public async Task Each_app_redirects_to_the_provider_with_its_own_client_and_returns_to_its_own_callback(string appId)
    {
        // F-02.
        AppDefinition app = appId == "app1" ? App1 : App2;
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(app, start: "/bff/login?returnUrl=/reports");

        Assert.Equal(302, login.Challenge.Status);
        Assert.Equal(gateway.Idp.AuthorizeEndpoint, new Uri(login.Challenge.Location).GetLeftPart(UriPartial.Path));
        Assert.Equal(app.OAuth.ClientId, login.Challenge.RedirectQuery["client_id"]);
        Assert.Equal(IdentityGateway.RedirectUriOf(app), login.Challenge.RedirectQuery["redirect_uri"]);

        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/reports", login.Callback.Location);
        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        Assert.Equal(app.AppId, record.AppId);
        Assert.Equal(app.OAuth.ClientId, record.ClientId);
    }

    [Theory]
    [InlineData("//evil.test")]
    [InlineData("https://evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("\\\\evil.test")]
    [InlineData("%2F%2Fevil.test")]
    [InlineData("evil.test")]
    [InlineData("")]
    public async Task A_return_url_that_is_not_a_local_path_becomes_the_root(string returnUrl)
    {
        // N-15. Each value goes on the query encoded, so what the endpoint reads is the value itself;
        // %2F%2Fevil.test is what a doubly encoded //evil.test reads as, which is not a path at all.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task An_encoded_backslash_stays_encoded_and_names_a_path_on_the_apps_own_host()
    {
        // A browser does not decode %5C in a Location header before it reads the authority, so this is
        // a path, not //evil.test.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=" + Uri.EscapeDataString("/%5Cevil.test"));

        Assert.Equal("/%5Cevil.test", login.Callback.Location);
        Assert.Equal(App1.Host, new Uri(new Uri($"https://{App1.Host}/"), login.Callback.Location).Host);
    }

    [Fact]
    public async Task Two_return_urls_are_not_a_local_path()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=/a&returnUrl=//evil.test");

        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task Without_a_return_url_the_login_returns_to_the_root()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login");

        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task A_login_discards_the_session_the_browser_already_has()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login first = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer login = await gateway.SendAsync(App1, "/bff/login?returnUrl=/", first.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(302, login.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, login.Location, StringComparison.Ordinal);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Contains(login.Cookies, c =>
            c.Name == first.Callback.SessionCookie.Name && c.Expires is { } expires && expires < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_relogin_discard_removes_the_session_under_the_turn_before_the_sign_out()
    {
        // #166 box 2, #196: the discard is the session module's end through the turn (AppSessions.EndAsync
        // with no action, #141 Q9), and the cookie handler's sign-out that follows finds the session gone.
        // The sign-out's ungated removal would remove the session too, so "the session is gone" cannot
        // tell the two apart; whether a gate was held as the one removal arrived can. Mutation: delete
        // `await turn.EndAsync();` from Bff.LoginAsync, keeping the turn; the turn is released with
        // the session still stored, the sign-out removes it with no gate held, and this fails.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login first = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        AppSessions sessions = gateway.Services.GetRequiredService<AppSessions>();
        gateway.Sessions.GateHeld = () => sessions.ActiveGates > 0;

        IdentityGateway.Answer login = await gateway.SendAsync(App1, "/bff/login?returnUrl=/", first.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(302, login.Status);
        Assert.Equal((key, true), Assert.Single(gateway.Sessions.GatedRemovals));
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_relogin_discard_waits_behind_a_refresh_in_flight_on_the_same_session()
    {
        // #166: the discard runs under the session gate (AppSessions.EndAsync, #141 Q9), so a refresh in
        // flight on the session finishes and stores its rotated tokens before the session is removed, and
        // the challenge follows the removal. A bare sign-out also removes the session, through the ticket
        // store, so "the session is gone" cannot tell the two apart; the record the removal found can.
        // The provider holds the refresh for 500 ms (its token delay, as the parallel-calls test uses),
        // and the re-login is sent once the refresh holds the gate. Mutation: discard outside the gate
        // (drop the EnterAsync and EndAsync block from Bff.LoginAsync, leaving the sign-out to remove);
        // the removal lands while the refresh holds the turn, finds the login's refresh token, and this
        // fails.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        gateway.Idp.Faults.AccessTokenLifetime = TimeSpan.FromSeconds(30);
        IdentityGateway.Login first = await gateway.LoginAsync(App1);
        gateway.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        (string key, _) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(500);

        Task<IdentityGateway.Answer> refresh = Task.Run(
            () => gateway.SendAsync(App1, IdentityGateway.TokenPath, first.Jar), TestContext.Current.CancellationToken);
        await gateway.GateHeldAsync(refresh);
        IdentityGateway.Answer login = await gateway.SendAsync(App1, "/bff/login?returnUrl=/", first.Jar, headers: IdentityGateway.Navigation);
        IReadOnlyList<(string Key, SessionRecord? Held)> removedByTheChallenge = gateway.Sessions.Removed;
        IdentityGateway.Answer refreshed = await refresh;

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        (string removedKey, SessionRecord? held) = Assert.Single(removedByTheChallenge);
        Assert.Equal(key, removedKey);
        Assert.Equal(grant.RefreshTokenIssued, RefreshTokenOf(Assert.IsType<SessionRecord>(held)));
        Assert.Equal((200, grant.AccessToken), (refreshed.Status, refreshed.Body));
        Assert.Equal(302, login.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, login.Location, StringComparison.Ordinal);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Contains(login.Cookies, c =>
            c.Name == first.Callback.SessionCookie.Name && c.Expires is { } expires && expires < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_relogin_over_a_cookie_for_no_session_still_challenges_and_carries_no_rule_id()
    {
        // #199 (#185 Q18 b): a re-login over a cookie that resolves to no session is answered as the login
        // was asked, with the challenge to the provider, so it ends in no rejection, and contracts/telemetry.md
        // has the rule id written only by the stage that ends the request with one; its session_ref still
        // names the session the cookie named, as the logout's does. Nothing is removed, since there is
        // nothing to discard. Mutation: let the login's KeyOf call note a dead cookie as the refusing
        // callers' do (drop refusesDeadSession: false); the login carries auth.session, and this fails.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login first = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        await gateway.Sessions.RemoveAsync(key, TestContext.Current.CancellationToken);

        IdentityGateway.Answer login = await gateway.SendAsync(App1, "/bff/login?returnUrl=/", first.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(302, login.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, login.Location, StringComparison.Ordinal);
        Assert.Null(login.Rule);
        Assert.Equal(SessionRef.Of(key), login.SessionRef);

        // The one removal the store saw is this test's own, above: the login's sign-out found no session
        // and removed nothing (AppSessions.RemoveAsync).
        Assert.Equal(key, Assert.Single(gateway.Sessions.Removed).Key);
    }

    [Fact]
    public async Task Its_response_bodies_hold_no_token()
    {
        // N-23 for /bff/login: the challenge and the callback it leads to.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=/");

        TokenSearch.AssertNoTokens(gateway.Idp, login.Challenge.Body + login.Callback.Body);
    }

    private static string? RefreshTokenOf(SessionRecord record) =>
        TicketSerializer.Default.Deserialize(record.Ticket.ToArray())?.Properties.GetTokenValue("refresh_token");
}
