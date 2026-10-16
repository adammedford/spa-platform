using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The cookie handler's <see cref="ITicketStore"/> for one app (plan row 3.3, ADR-0004): it turns a
/// ticket into a <see cref="SessionRecord"/> in <see cref="ISessionStore"/> and back. With a session
/// store set, the cookie carries only the session key and the ticket with its saved tokens stays on
/// the server (S-B3, R-A8).
///
/// Every record is bound to the app and the client this adapter was built for, and a record bound to
/// anything else is treated as absent: it is not returned, renewed or removed. So a validly
/// encrypted cookie for one app that names another app's session key is anonymous (S-B7, N-02), and
/// a session established with a client the app has since left is too (task 3.6).
///
/// This is also where workstream 3 writes <c>session_ref</c> (contracts/telemetry.md), because it is
/// the one place that holds both the session key and the request. The key itself goes nowhere else:
/// it is on that contract's never-logged list, and only <see cref="Abstractions.SessionRef.Of"/>'s
/// result reaches <see cref="IRequestTelemetryFeature"/>.
///
/// The cookie handler calls the overloads that carry the request; it has since .NET 7:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs.
/// The overloads without one are refused rather than guessed at. The store is read from the request's
/// services when a session is first touched, not when options are built, so a request that carries no
/// session cookie never needs one.
/// </summary>
internal sealed class AppBoundTicketStore(string appId, string clientId) : ITicketStore
{
    private const string NoRequest = "The session store is only used with the request the cookie handler passes.";

    public Task<string> StoreAsync(AuthenticationTicket ticket) => throw new NotSupportedException(NoRequest);

    public Task RenewAsync(string key, AuthenticationTicket ticket) => throw new NotSupportedException(NoRequest);

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => throw new NotSupportedException(NoRequest);

    public Task RemoveAsync(string key) => throw new NotSupportedException(NoRequest);

    /// <summary>A new session under a new key. Sign-in on the callback always lands here (R-D6).</summary>
    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        DateTimeOffset createdAt = ticket.Properties.IssuedUtc
            ?? throw new InvalidOperationException("The cookie handler signs in with an issue time.");
        string key = await Store(httpContext).CreateAsync(Record(ticket, createdAt, version: 0), cancellationToken);
        Reference(httpContext, key);
        return key;
    }

    /// <summary>
    /// Replaces the ticket under a key this app owns, one version on. A key it does not own is left
    /// alone. A replace that loses a race to a newer write leaves the newer record, which is the one a
    /// later read should see.
    /// </summary>
    public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Reference(httpContext, key);
        ISessionStore store = Store(httpContext);
        if (await Owned(store, key, cancellationToken) is { } current)
        {
            await store.ReplaceAsync(key, Record(ticket, current.CreatedAt, current.Version + 1), current.Version, cancellationToken);
        }
    }

    /// <summary>The ticket under a key this app owns, or null: an unknown key and another app's key look the same.</summary>
    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Reference(httpContext, key);
        return await Owned(Store(httpContext), key, cancellationToken) is { } record
            ? TicketSerializer.Default.Deserialize(record.Ticket.ToArray())
            : null;
    }

    /// <summary>Removes a session this app owns. Another app's key is left alone.</summary>
    public async Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Reference(httpContext, key);
        ISessionStore store = Store(httpContext);
        if (await Owned(store, key, cancellationToken) is not null)
        {
            await store.RemoveAsync(key, cancellationToken);
        }
    }

    private SessionRecord Record(AuthenticationTicket ticket, DateTimeOffset createdAt, long version) => new(
        AppId: appId,
        ClientId: clientId,
        // MapInboundClaims is off (task 3.2), so the provider's own claim name reaches the principal.
        Subject: ticket.Principal.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("A signed-in principal carries the provider's sub claim."),
        CreatedAt: createdAt,
        // The cookie handler sets the expiry from ExpireTimeSpan, the entry's lifetime, at sign-in.
        ExpiresAt: ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException("The cookie handler signs in with an expiry."),
        Version: version,
        Ticket: TicketSerializer.Default.Serialize(ticket));

    private async Task<SessionRecord?> Owned(ISessionStore store, string key, CancellationToken cancellationToken) =>
        await store.GetAsync(key, cancellationToken) is { } record
        && string.Equals(record.AppId, appId, StringComparison.Ordinal)
        && string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
            ? record
            : null;

    private static ISessionStore Store(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ISessionStore>();

    /// <summary>
    /// The request's <c>session_ref</c>: the key this request presented or was given. Null-conditional,
    /// because a component test runs without the telemetry stage.
    /// </summary>
    private static void Reference(HttpContext httpContext, string key) =>
        httpContext.Features.Get<IRequestTelemetryFeature>()?.SessionRef = SessionRef.Of(key);
}
