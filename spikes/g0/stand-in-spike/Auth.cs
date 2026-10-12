using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Spike;

/// <summary>
/// The whole "dynamic scheme" mechanism. Schemes are not registered or removed: they are a pure function of
/// (current request host, registry snapshot). A request on host A can only ever see A's two schemes.
/// </summary>
public sealed class RegistrySchemeProvider(IHttpContextAccessor accessor, Registry registry) : IAuthenticationSchemeProvider
{
    private static readonly Task<AuthenticationScheme?> None = Task.FromResult<AuthenticationScheme?>(null);
    private static readonly Task<IEnumerable<AuthenticationScheme>> Empty = Task.FromResult<IEnumerable<AuthenticationScheme>>([]);

    private AppEntry? App => accessor.HttpContext is { } ctx ? RequestApp.Resolve(ctx, registry) : null;

    private static AuthenticationScheme Cookie(AppEntry a) => new(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler));
    private static AuthenticationScheme Oidc(AppEntry a) => new(SchemeNames.Oidc(a.AppId), null, typeof(StandInOidcHandler));
    private static Task<AuthenticationScheme?> T(AuthenticationScheme? s) => s is null ? None : Task.FromResult<AuthenticationScheme?>(s);

    public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => T(App is { } a ? Oidc(a) : null);

    public Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (App is not { } a) return None;
        if (string.Equals(name, SchemeNames.Cookie(a.AppId), StringComparison.Ordinal)) return T(Cookie(a));
        if (string.Equals(name, SchemeNames.Oidc(a.AppId), StringComparison.Ordinal)) return T(Oidc(a));
        return None; // another app's scheme name does not exist on this host
    }

    // AuthenticationMiddleware asks this on EVERY request and instantiates each handler returned.
    public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Oidc(a)]) : Empty;

    public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Cookie(a), Oidc(a)]) : Empty;

    public void AddScheme(AuthenticationScheme scheme) => throw new NotSupportedException("Schemes come from the registry only.");
    public void RemoveScheme(string name) => throw new NotSupportedException("Schemes come from the registry only.");
}

/// <summary>Builds per-app cookie options when the framework first asks for "cookie:{appId}".</summary>
public sealed class CookieOptionsSetup(Registry registry, SessionStore store) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(CookieAuthenticationOptions options) { }

    public void Configure(string? name, CookieAuthenticationOptions o)
    {
        if (!SchemeNames.TryParseCookie(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        o.Cookie.Name = "__Host-spa-session";            // host-only by prefix rule: Secure, Path=/, no Domain
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.SessionStore = new AppBoundTicketStore(app.AppId, app.ClientId, store);
        // TicketDataFormat is left null on purpose: the framework's post-configure derives the protector
        // purpose from the scheme name, so app A's cookie cannot be unprotected under app B's scheme.
    }
}

/// <summary>Builds per-app client options when the framework first asks for "oidc:{appId}".</summary>
public sealed class OidcOptionsSetup(Registry registry, IdpEndpoint idp, SharedBackchannel backchannel) : IConfigureNamedOptions<StandInOidcOptions>
{
    public void Configure(StandInOidcOptions options) { }

    public void Configure(string? name, StandInOidcOptions o)
    {
        if (!SchemeNames.TryParseOidc(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        o.Authority = idp.BaseUrl;
        o.ClientId = app.ClientId;
        o.ClientSecret = app.ClientSecret;
        o.Scope = app.Scope;
        o.CallbackPath = "/signin-oidc";                 // SAME path for every app, on purpose
        o.SignInScheme = SchemeNames.Cookie(app.AppId);  // must be explicit: the framework default is process-wide
        o.Backchannel = backchannel.Client;              // one HttpClient for all apps (the real handler otherwise creates one per scheme)
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    }
}

public sealed record IdpEndpoint(string BaseUrl);
public sealed class SharedBackchannel { public HttpClient Client { get; } = new(); }

/// <summary>Evicts cached options for apps whose entry changed or vanished. Runs after the snapshot swap.</summary>
public sealed class OptionsInvalidator
{
    public int Evictions;

    public OptionsInvalidator(Registry registry, SessionStore store,
        IOptionsMonitorCache<CookieAuthenticationOptions> cookies, IOptionsMonitorCache<StandInOidcOptions> oidc)
    {
        registry.Swapped += (old, next) =>
        {
            foreach (var appId in old.ByAppId.Keys.Union(next.ByAppId.Keys))
            {
                old.ByAppId.TryGetValue(appId, out var before);
                next.ByAppId.TryGetValue(appId, out var after);
                if (Equals(before, after)) continue;       // record equality: untouched apps keep their cached options
                cookies.TryRemove(SchemeNames.Cookie(appId));
                oidc.TryRemove(SchemeNames.Oidc(appId));
                Interlocked.Increment(ref Evictions);
                if (after is null) store.RemoveApp(appId); // app removed: drop its sessions too
            }
        };
    }
}

/// <summary>Server-side session records. Tickets are stored serialized, never as live shared objects.</summary>
public sealed class SessionStore
{
    public sealed record Record(string AppId, string ClientId, byte[] Ticket);

    private readonly ConcurrentDictionary<string, Record> _items = new(StringComparer.Ordinal);

    public int Count => _items.Count;
    public IReadOnlyCollection<Record> All => _items.Values.ToList();
    public Record? TryGet(string key) => _items.TryGetValue(key, out var r) ? r : null;
    public void Set(string key, Record record) => _items[key] = record;
    public bool Remove(string key) => _items.TryRemove(key, out _);
    public int CountFor(string appId) => _items.Values.Count(r => r.AppId == appId);
    public IReadOnlyList<string> KeysFor(string appId) => _items.Where(kv => kv.Value.AppId == appId).Select(kv => kv.Key).ToList();

    public void RemoveApp(string appId)
    {
        foreach (var kv in _items) if (kv.Value.AppId == appId) _items.TryRemove(kv.Key, out _);
    }
}

/// <summary>ITicketStore bound to one app and one OAuth client. A key minted for another app or client reads as "no session".</summary>
public sealed class AppBoundTicketStore(string appId, string clientId, SessionStore store) : ITicketStore
{
    public static readonly object SessionKeyItem = new();

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        store.Set(key, new SessionStore.Record(appId, clientId, TicketSerializer.Default.Serialize(ticket)));
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (Owned(key) is not null) store.Set(key, new SessionStore.Record(appId, clientId, TicketSerializer.Default.Serialize(ticket)));
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
        => Task.FromResult(Owned(key) is { } r ? TicketSerializer.Default.Deserialize(r.Ticket) : null);

    // The HttpContext overload (added in .NET 8) lets the token accessor learn the session key for this request.
    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var record = Owned(key);
        if (record is null) return Task.FromResult<AuthenticationTicket?>(null);
        httpContext.Items[SessionKeyItem] = key;
        return Task.FromResult(TicketSerializer.Default.Deserialize(record.Ticket));
    }

    public Task RemoveAsync(string key)
    {
        if (Owned(key) is not null) store.Remove(key);
        return Task.CompletedTask;
    }

    private SessionStore.Record? Owned(string key)
        => store.TryGet(key) is { } r && r.AppId == appId && r.ClientId == clientId ? r : null;
}
