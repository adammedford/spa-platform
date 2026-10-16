using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.3 with task 3.2: a whole login on each of two apps that share one callback path, and what
/// one app's session is worth on the other's host. The seam is the request through the framework's
/// <c>UseAuthentication</c> and the session store's own interface.
/// </summary>
public sealed class SessionIsolationTests
{
    private static readonly AppDefinition App1 = TestApps.Entry(
        "app1", issuer: IdentityGateway.IssuerName, resource: "https://api.app1.spa-poc.test");

    private static readonly AppDefinition App2 = TestApps.Entry(
        "app2", issuer: IdentityGateway.IssuerName, resource: "https://api.app2.spa-poc.test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("app1")]
    [InlineData("app2")]
    public async Task A_login_ends_on_the_apps_shell_signed_in_through_its_own_scheme_and_client(string appId)
    {
        // S-A4 and R-A3 (app 1), S-A5 and R-A4 (app 2, on the same callback path), S-A6 and R-A5 (its own
        // default scheme, tokens issued to its own client with its resource).
        AppDefinition app = appId == "app1" ? App1 : App2;
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(app);
        Assert.Equal("/", login.Callback.Location);

        IdentityGateway.Answer shell = await gateway.SendAsync(app, "/", login.Jar);
        Assert.Equal(200, shell.Status);
        Assert.Equal("alice", shell.Subject);
        Assert.Equal(200, (await gateway.SendAsync(app, IdentityGateway.ApiPath, login.Jar)).Status);

        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        AuthenticationTicket stored = TicketSerializer.Default.Deserialize(record.Ticket.ToArray())!;
        Assert.Equal(SchemeNames.Cookie(app.AppId), stored.AuthenticationScheme);
        var accessToken = TestIdentityProvider.ReadJwt(stored.Properties.GetTokenValue("access_token")!);
        Assert.Equal(app.OAuth.ClientId, accessToken.GetClaim("client_id").Value);
        Assert.Equal([app.OAuth.Resource!], accessToken.Audiences);
    }

    [Fact]
    public async Task After_login_the_browser_holds_only_the_session_cookie()
    {
        // S-B2 and R-A9: the callback deletes both login-state cookies and sets the session cookie.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        Dictionary<string, string> jar = new(StringComparer.Ordinal);
        foreach (SetCookieHeaderValue set in login.Challenge.Cookies.Concat(login.Callback.Cookies))
        {
            if (set.Expires is { } expires && expires < DateTimeOffset.UtcNow)
            {
                jar.Remove(set.Name.Value!);
            }
            else
            {
                jar[set.Name.Value!] = set.Value.Value!;
            }
        }

        Assert.Equal(["__Host-spa-session"], jar.Keys);
    }

    [Fact]
    public async Task App1s_cookie_cannot_be_decrypted_by_app2_and_is_anonymous_there()
    {
        // S-B5, S-B6, R-A10 and N-01: the same cookie name on both hosts, but each scheme's data protection
        // purpose carries its name, so app 2 cannot read app 1's cookie. The API answers 401 and a
        // navigation is challenged afresh, for app 2's own client.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        Assert.Null(CookieOptions(gateway, App2).TicketDataFormat.Unprotect(login.Callback.SessionCookie.Value.Value));

        Assert.Equal(401, (await gateway.SendAsync(App2, IdentityGateway.ApiPath, login.Jar)).Status);
        IdentityGateway.Answer navigation = await gateway.SendAsync(App2, "/", login.Jar);
        Assert.Equal(302, navigation.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, navigation.Location, StringComparison.Ordinal);
        Assert.Equal("client-app2", navigation.RedirectQuery["client_id"]);
        Assert.Null(navigation.Subject);
    }

    [Fact]
    public async Task A_validly_encrypted_app2_cookie_naming_app1s_session_key_is_anonymous()
    {
        // S-B7 and N-02: the cookie decrypts, but the store is bound to app and client.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        await gateway.LoginAsync(App1);
        (string app1Key, _) = await gateway.Sessions.SingleAsync();

        string forged = Forge(gateway, App2, app1Key);
        Assert.NotNull(CookieOptions(gateway, App2).TicketDataFormat.Unprotect(forged));
        Dictionary<string, string> jar = new() { ["__Host-spa-session"] = forged };

        Assert.Equal(401, (await gateway.SendAsync(App2, IdentityGateway.ApiPath, jar)).Status);
        Assert.Null((await gateway.SendAsync(App2, "/", jar)).Subject);

        // The control: the same forgery for app 1 itself is accepted, so it is the binding that refused.
        Dictionary<string, string> own = new() { ["__Host-spa-session"] = Forge(gateway, App1, app1Key) };
        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, own)).Status);

        // And app 2 left app 1's session where it was.
        Assert.NotNull(await gateway.Sessions.GetAsync(app1Key, Ct));
    }

    [Fact]
    public async Task A_session_established_with_the_apps_previous_client_is_anonymous_once_the_app_moves_client()
    {
        // S-B4: the record is bound to the client as well as the app (ISessionStore, SessionRecord). The
        // options are evicted by hand here, as task 3.6's invalidator does on a fingerprint change.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        AppDefinition moved = App1 with { OAuth = App1.OAuth with { ClientId = "client-app1-next" }, Fingerprint = "sha256:moved" };

        gateway.Services.GetRequiredService<IOptionsMonitorCache<CookieAuthenticationOptions>>().TryRemove(SchemeNames.Cookie(App1.AppId));

        Assert.NotNull(CookieOptions(gateway, moved).TicketDataFormat.Unprotect(login.Callback.SessionCookie.Value.Value));
        Assert.Equal(401, (await gateway.SendAsync(moved, IdentityGateway.ApiPath, login.Jar)).Status);
        Assert.NotNull(await gateway.Sessions.GetAsync(Assert.Single(gateway.Sessions.Created), Ct));
    }

    [Fact]
    public async Task A_cookie_whose_session_was_removed_is_401()
    {
        // N-21: decryption succeeds; the store lookup does not.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, login.Jar)).Status);

        (string key, _) = await gateway.Sessions.SingleAsync();
        await gateway.Sessions.RemoveAsync(key, Ct);

        Assert.NotNull(CookieOptions(gateway, App1).TicketDataFormat.Unprotect(login.Callback.SessionCookie.Value.Value));
        Assert.Equal(401, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, login.Jar)).Status);
    }

    [Fact]
    public async Task A_login_over_a_planted_session_cookie_creates_a_new_key_and_the_planted_one_keeps_its_identity()
    {
        // N-18 and R-D6 (ADR-0004, T12): the callback does not see the session cookie, so sign-in stores
        // a new session instead of renewing the planted one.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login planter = await gateway.LoginAsync(App1);
        string planted = Assert.Single(gateway.Sessions.Created);

        gateway.Idp.DefaultSubject = "bob";
        IdentityGateway.Login victim = await gateway.LoginAsync(App1, planted: planter.Jar);

        Assert.Equal(2, gateway.Sessions.Created.Count);
        string issued = gateway.Sessions.Created[1];
        Assert.NotEqual(planted, issued);
        Assert.Equal("alice", (await gateway.Sessions.GetAsync(planted, Ct))!.Subject);
        Assert.Equal("bob", (await gateway.Sessions.GetAsync(issued, Ct))!.Subject);

        Assert.Equal("alice", (await gateway.SendAsync(App1, "/", planter.Jar)).Subject);
        Assert.Equal("bob", (await gateway.SendAsync(App1, "/", victim.Jar)).Subject);
    }

    [Fact]
    public async Task The_session_ref_of_the_sessions_key_is_left_on_the_request()
    {
        // contracts/telemetry.md: session_ref, written by workstream 3 where it holds the key and the
        // request, on the sign-in and on every request that presents the session.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        Assert.Equal(SessionRef.Of(key), login.Callback.SessionRef);
        Assert.Equal(SessionRef.Of(key), (await gateway.SendAsync(App1, IdentityGateway.ApiPath, login.Jar)).SessionRef);
        Assert.Null((await gateway.SendAsync(App1, IdentityGateway.ApiPath)).SessionRef);
    }

    private static CookieAuthenticationOptions CookieOptions(IdentityGateway gateway, AppDefinition app)
    {
        gateway.Pin(app);
        return gateway.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SchemeNames.Cookie(app.AppId));
    }

    /// <summary>A cookie <paramref name="app"/>'s scheme encrypted itself, naming <paramref name="sessionKey"/>.</summary>
    private static string Forge(IdentityGateway gateway, AppDefinition app, string sessionKey)
    {
        string scheme = SchemeNames.Cookie(app.AppId);
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", sessionKey)], scheme));
        AuthenticationProperties properties = new()
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        };
        return CookieOptions(gateway, app).TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, scheme));
    }
}
