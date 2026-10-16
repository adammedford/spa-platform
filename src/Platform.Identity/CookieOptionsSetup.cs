using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// Builds one app's session cookie options when the framework first asks for its cookie scheme by
/// name (plan row 3.3, ADR-0001, ADR-0004). A name that matches no app is left as the framework
/// builds it.
/// </summary>
public sealed class CookieOptionsSetup(IHttpContextAccessor accessor, IRegistry registry)
    : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    /// <summary>The session cookie's name, the same on every host: the host is what tells two apps apart.</summary>
    public const string CookieName = "__Host-spa-session";

    /// <summary>What every cookie scheme name starts with: <see cref="SchemeNames.Cookie(string)"/> of no app.</summary>
    internal static readonly string Prefix = SchemeNames.Cookie(string.Empty);

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || SchemeApp.Find(accessor, registry, name[Prefix.Length..]) is not { } app)
        {
            return;
        }

        // __Host- requires Secure, Path=/ and no Domain, so a sibling host can neither plant nor
        // overwrite it (S-B1, ADR-0007). Lax rather than Strict: the callback is a top-level navigation
        // from the provider, and the first request after it must carry the cookie.
        options.Cookie.Name = CookieName;
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // The entry's lifetime, fixed at sign-in: no sliding expiry, so activity never extends a
        // session past what the registry entry allows (registry.schema.json#/$defs/session).
        options.ExpireTimeSpan = app.SessionLifetime;
        options.SlidingExpiration = false;

        // The ticket and its saved tokens on the server, bound to this app and client (ADR-0004).
        options.SessionStore = new AppBoundTicketStore(app.AppId, app.OAuth.ClientId);

        // The callback never sees an existing session cookie, so a login always gets a new key (R-D6).
        options.CookieManager = new CallbackBlindCookieManager(OidcOptionsSetup.CallbackPath);
    }
}
