using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Spike;

// Same mechanism as the spike's Auth.cs. The only substantive change: the remote handler is the REAL
// Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler, not the stand-in.

public sealed class RegistrySchemeProvider(IHttpContextAccessor accessor, Registry registry) : IAuthenticationSchemeProvider
{
    private static readonly Task<AuthenticationScheme?> None = Task.FromResult<AuthenticationScheme?>(null);
    private static readonly Task<IEnumerable<AuthenticationScheme>> Empty = Task.FromResult<IEnumerable<AuthenticationScheme>>([]);

    private AppEntry? App => accessor.HttpContext is { } ctx ? RequestApp.Resolve(ctx, registry) : null;

    private static AuthenticationScheme Cookie(AppEntry a) => new(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler));
    private static AuthenticationScheme Oidc(AppEntry a) => new(SchemeNames.Oidc(a.AppId), null, typeof(OpenIdConnectHandler));
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
        return None;
    }

    public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Oidc(a)]) : Empty;

    public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
        => App is { } a ? Task.FromResult<IEnumerable<AuthenticationScheme>>([Cookie(a), Oidc(a)]) : Empty;

    public void AddScheme(AuthenticationScheme scheme) => throw new NotSupportedException("Schemes come from the registry only.");
    public void RemoveScheme(string name) => throw new NotSupportedException("Schemes come from the registry only.");
}

/// <summary>Knobs for the review probes. Defaults reproduce the plan as written.</summary>
public sealed class Switches
{
    public Mode Mode = Mode.HostScoped;
    public bool SkipUnrecognizedRequests;            // OpenIdConnectOptions.SkipUnrecognizedRequests
    public bool DisableRemoteSignOutPath;            // RemoteSignOutPath = default
    public bool HostPrefixedLoginCookies;            // __Host- names and Path=/ on correlation and nonce cookies
    public bool BlindSessionCookieOnCallback;        // ICookieManager that hides the session cookie on the callback path
    public bool ShareMetadata = true;                // one ConfigurationManager for every app
    public string? ResponseMode = "query";           // null = leave the framework default (form_post)
    public bool AntiforgerySecureAlways = true;
    public bool AntiforgerySuppressFormBody = true;
    public bool AntiforgeryBindApp;
}

public sealed record IdpEndpoint(string BaseUrl);

/// <summary>One backchannel and one metadata manager for every app on the issuer (plan task 3.2).</summary>
public sealed class SharedOidc
{
    public HttpClient Backchannel { get; } = new();
    public IConfigurationManager<OpenIdConnectConfiguration> Metadata { get; }

    public SharedOidc(IdpEndpoint idp)
        => Metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            idp.BaseUrl + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(Backchannel) { RequireHttps = false });   // loopback test provider only
}

public sealed class CookieOptionsSetup(Registry registry, SessionStore store, Switches sw) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(CookieAuthenticationOptions options) { }

    public void Configure(string? name, CookieAuthenticationOptions o)
    {
        if (!SchemeNames.TryParseCookie(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        o.Cookie.Name = "__Host-spa-session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.SessionStore = new AppBoundTicketStore(app.AppId, app.ClientId, store);
        if (sw.BlindSessionCookieOnCallback) o.CookieManager = new CallbackBlindCookieManager(new ChunkingCookieManager(), "/signin-oidc");
    }
}

/// <summary>
/// Candidate fix for session-key reuse: on the OIDC callback the cookie handler never sees an incoming session
/// cookie, so HandleSignInAsync always calls ITicketStore.StoreAsync (new key) instead of RenewAsync (old key).
/// </summary>
public sealed class CallbackBlindCookieManager(ICookieManager inner, PathString callbackPath) : ICookieManager
{
    public string? GetRequestCookie(HttpContext context, string key) => context.Request.Path == callbackPath ? null : inner.GetRequestCookie(context, key);
    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) => inner.AppendResponseCookie(context, key, value, options);
    public void DeleteCookie(HttpContext context, string key, CookieOptions options) => inner.DeleteCookie(context, key, options);
}

public sealed class OidcOptionsSetup(Registry registry, IdpEndpoint idp, SharedOidc shared, Switches sw) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) { }

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (!SchemeNames.TryParseOidc(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        o.ClientId = app.ClientId;
        o.ClientSecret = app.ClientSecret;
        o.ResponseType = OpenIdConnectResponseType.Code;
        if (sw.ResponseMode is not null) o.ResponseMode = sw.ResponseMode;
        o.UsePkce = true;
        o.SaveTokens = true;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        foreach (var s in app.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(s);
        o.Resource = app.Resource;
        o.CallbackPath = "/signin-oidc";                 // same path for every app
        o.SignInScheme = SchemeNames.Cookie(app.AppId);
        o.Backchannel = shared.Backchannel;
        if (sw.ShareMetadata) o.ConfigurationManager = shared.Metadata;
        else { o.Authority = idp.BaseUrl; o.RequireHttpsMetadata = false; }
        o.TokenValidationParameters.NameClaimType = "sub";
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        if (sw.HostPrefixedLoginCookies)
        {
            o.CorrelationCookie.Name = "__Host-spa-corr."; o.CorrelationCookie.Path = "/";
            o.NonceCookie.Name = "__Host-spa-nonce."; o.NonceCookie.Path = "/";
        }
        if (sw.DisableRemoteSignOutPath) o.RemoteSignOutPath = default;
        o.SkipUnrecognizedRequests = sw.SkipUnrecognizedRequests;
    }
}

public sealed class OptionsInvalidator
{
    public OptionsInvalidator(Registry registry, SessionStore store,
        IOptionsMonitorCache<CookieAuthenticationOptions> cookies, IOptionsMonitorCache<OpenIdConnectOptions> oidc)
    {
        registry.Swapped += (old, next) =>
        {
            foreach (var appId in old.ByAppId.Keys.Union(next.ByAppId.Keys))
            {
                old.ByAppId.TryGetValue(appId, out var before);
                next.ByAppId.TryGetValue(appId, out var after);
                if (Equals(before, after)) continue;
                cookies.TryRemove(SchemeNames.Cookie(appId));
                oidc.TryRemove(SchemeNames.Oidc(appId));
                if (after is null) store.RemoveApp(appId);
            }
        };
    }
}

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
    public string? SubOf(string key) => TryGet(key) is { } r ? TicketSerializer.Default.Deserialize(r.Ticket)!.Principal.FindFirst("sub")?.Value : null;

    public void RemoveApp(string appId)
    {
        foreach (var kv in _items) if (kv.Value.AppId == appId) _items.TryRemove(kv.Key, out _);
    }
}

public sealed class AppBoundTicketStore(string appId, string clientId, SessionStore store) : ITicketStore
{
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

    public Task RemoveAsync(string key)
    {
        if (Owned(key) is not null) store.Remove(key);
        return Task.CompletedTask;
    }

    private SessionStore.Record? Owned(string key)
        => store.TryGet(key) is { } r && r.AppId == appId && r.ClientId == clientId ? r : null;
}

/// <summary>Plan task 4.4: "App id bound into the token".</summary>
public sealed class AppBoundAntiforgeryData(Registry registry) : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) => RequestApp.Resolve(context, registry)?.AppId ?? "";
    public bool ValidateAdditionalData(HttpContext context, string additionalData) => additionalData == (RequestApp.Resolve(context, registry)?.AppId ?? "");
}

public sealed class DemoOptions { }
