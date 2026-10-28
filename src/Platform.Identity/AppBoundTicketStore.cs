using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The cookie handler's <see cref="ITicketStore"/> for one app (plan row 3.3, ADR-0004): it turns a
/// ticket into a <see cref="SessionRecord"/> and back. With a session store set, the cookie carries only
/// the session key and the ticket with its saved tokens stays on the server (S-B3, R-A8).
///
/// An adapter and nothing more. It opens, reads and removes through <see cref="AppSessions"/>, passing
/// the app it was built for to the module's one ownership check, so a record bound to anything else is
/// treated as absent: it is not returned or removed. A validly encrypted cookie for one app that names
/// another app's session key is anonymous (S-B7, N-02), and so is a session established with a client
/// the app has since left (task 3.6). The module also writes <c>session_ref</c> and sets the request's
/// key channel; nothing here does either, and the key <c>StoreAsync</c> returns is the module's answer
/// passed on to the cookie handler.
///
/// Its reads and removes stay off the session gate on purpose: the handler reads through here on every
/// request that presents a session, and gating that read would serialise every request on a session
/// behind a refresh (#141).
///
/// The cookie handler calls the overloads that carry the request; it has since .NET 7:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs.
/// The overloads without one are refused rather than guessed at. The module is read from the request's
/// services when a session is first touched, not when options are built, so a request that carries no
/// session cookie never builds the session store behind it.
/// </summary>
internal sealed class AppBoundTicketStore(AppDefinition app) : ITicketStore
{
    private const string NoRequest = "The session store is only used with the request the cookie handler passes.";

    private const string NoRenew =
        "A session is never renewed through the ticket store: every sign-in stores a new key (CallbackBlindCookieManager), "
        + "sliding expiry is off (CookieOptionsSetup), and no ValidatePrincipal event asks for a renewal. A renew here "
        + "would write the ticket this request started with over whatever a concurrent refresh stored.";

    public Task<string> StoreAsync(AuthenticationTicket ticket) => throw new NotSupportedException(NoRequest);

    public Task RenewAsync(string key, AuthenticationTicket ticket) => throw new NotSupportedException(NoRequest);

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => throw new NotSupportedException(NoRequest);

    public Task RemoveAsync(string key) => throw new NotSupportedException(NoRequest);

    /// <summary>A new session under a new key. Sign-in on the callback always lands here (R-D6).</summary>
    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        DateTimeOffset createdAt = ticket.Properties.IssuedUtc
            ?? throw new InvalidOperationException("The cookie handler signs in with an issue time.");
        return Sessions(httpContext).OpenAsync(httpContext, Record(ticket, createdAt), cancellationToken);
    }

    /// <summary>
    /// Refused (#141, Q11). The cookie handler renews through the store on three paths, and nothing
    /// takes any of them: a sign-in on a request that already carries a session key, which the
    /// callback-blind cookie manager prevents by making every sign-in store a new key; a sliding-expiry
    /// refresh, which is off; and a <c>ValidatePrincipal</c> event that sets <c>ShouldRenew</c>, where
    /// no cookie event is configured at all
    /// (CookieAuthenticationHandler.HandleSignInAsync, CheckForRefreshAsync and HandleAuthenticateAsync,
    /// each reaching <c>SessionStore.RenewAsync</c>:
    /// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs).
    /// If anything ever did, it would write the ticket captured at the request's start under a version
    /// read fresh, the ungated stale write <see cref="AppSessions"/> exists to remove. Throwing makes
    /// that path fail loudly.
    /// </summary>
    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NoRenew);

    /// <summary>The ticket under a key this app owns, or null: an unknown key and another app's key look the same.</summary>
    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        await Sessions(httpContext).ResolveAsync(httpContext, app, key, cancellationToken) is { } record
            ? TicketSerializer.Default.Deserialize(record.Ticket.ToArray())
            : null;

    /// <summary>
    /// Removes a session this app owns; another app's key is left alone, and a key already removed is a
    /// no-op, as is a key logout's end has already ended on this request (#386 Q192). The cookie handler
    /// calls this from its sign-out and when it finds a ticket it read expired.
    /// </summary>
    public Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        Sessions(httpContext).RemoveAsync(httpContext, app, key, cancellationToken);

    private SessionRecord Record(AuthenticationTicket ticket, DateTimeOffset createdAt) => new(
        AppId: app.AppId,
        ClientId: app.OAuth.ClientId,
        // MapInboundClaims is off (task 3.2), so the provider's own claim name reaches the principal.
        Subject: ticket.Principal.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("A signed-in principal carries the provider's sub claim."),
        CreatedAt: createdAt,
        // The cookie handler sets the expiry from ExpireTimeSpan, the entry's lifetime, at sign-in.
        ExpiresAt: ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException("The cookie handler signs in with an expiry."),
        Version: 0,
        Ticket: TicketSerializer.Default.Serialize(ticket));

    private static AppSessions Sessions(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<AppSessions>();
}
