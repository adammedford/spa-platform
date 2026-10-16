using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.3's cookie options through a whole login against the test identity provider: the session
/// cookie's name and attributes, its lifetime, and what it carries.
/// </summary>
public sealed class SessionCookieTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task The_session_cookie_is_host_prefixed_secure_http_only_lax_on_the_root_path_with_no_domain()
    {
        // S-B1. ADR-0007: __Host- is what stops a sibling host planting or overwriting it.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        SetCookieHeaderValue session = (await gateway.LoginAsync(App1)).Callback.SessionCookie;

        Assert.Equal("__Host-spa-session", session.Name.Value);
        Assert.True(session.Secure);
        Assert.True(session.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, session.SameSite);
        Assert.Equal("/", session.Path.Value);
        Assert.False(session.Domain.HasValue);
    }

    [Fact]
    public async Task The_cookie_carries_a_session_id_and_the_tokens_sit_in_the_store_bound_to_app_and_client()
    {
        // S-B3, S-B4 and R-A8 (ADR-0004): with SaveTokens and a ticket store the cookie holds the
        // session-id claim and nothing else; the access, refresh and ID tokens are in the session record,
        // which names the app and the client it was established with.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        SetCookieHeaderValue cookie = (await gateway.LoginAsync(App1)).Callback.SessionCookie;

        gateway.Pin(App1);
        CookieAuthenticationOptions options = gateway.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SchemeNames.Cookie(App1.AppId));
        AuthenticationTicket inCookie = Assert.IsType<AuthenticationTicket>(options.TicketDataFormat.Unprotect(cookie.Value.Value));
        Claim only = Assert.Single(inCookie.Principal.Claims);
        Assert.Equal("Microsoft.AspNetCore.Authentication.Cookies-SessionId", only.Type);
        Assert.DoesNotContain(inCookie.Properties.Items.Keys, k => k.StartsWith(".Token.", StringComparison.Ordinal));

        (string key, SessionRecord record) = await gateway.Sessions.SingleAsync();
        Assert.Equal(key, only.Value);
        Assert.Equal("app1", record.AppId);
        Assert.Equal("client-app1", record.ClientId);
        Assert.Equal("alice", record.Subject);

        AuthenticationTicket stored = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        Assert.Equal("alice", stored.Principal.FindFirst("sub")?.Value);
        Assert.Contains(gateway.Idp.IssuedAccessTokens, t => t == stored.Properties.GetTokenValue("access_token"));
        Assert.Contains(gateway.Idp.IssuedRefreshTokens, t => t == stored.Properties.GetTokenValue("refresh_token"));
        Assert.False(string.IsNullOrEmpty(stored.Properties.GetTokenValue("id_token")));
    }

    [Fact]
    public async Task The_session_lasts_the_entrys_lifetime_and_does_not_slide()
    {
        // Plan row 3.3: lifetime from the entry (registry.schema.json#/$defs/session), no sliding expiry.
        AppDefinition shortLived = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName, sessionLifetime: TimeSpan.FromMinutes(17));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shortLived, App2]);

        await gateway.LoginAsync(shortLived);

        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        Assert.Equal(TimeSpan.FromMinutes(17), record.ExpiresAt - record.CreatedAt);
        Assert.Equal(0, record.Version);

        gateway.Pin(shortLived);
        CookieAuthenticationOptions options = gateway.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SchemeNames.Cookie(shortLived.AppId));
        Assert.Equal(TimeSpan.FromMinutes(17), options.ExpireTimeSpan);
        Assert.False(options.SlidingExpiration);
    }
}
