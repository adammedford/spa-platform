using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Platform.Identity;

/// <summary>
/// The session cookie's <see cref="ICookieManager"/>: on the login callback path it reports no session
/// cookie, and everywhere else it is the framework's chunking manager (plan row 3.3, ADR-0004).
///
/// The cookie handler signs in by renewing the session it read from the request when there is one,
/// and by storing a new one otherwise. A session cookie planted before the login would then carry the
/// new identity under a key the planter already knows: session fixation (T12, N-18). Signing out
/// first in <c>/bff/login</c> does not close it (R-D5). Hidden here, the callback's sign-in always
/// stores, so every login gets a new key and the planted one keeps its old identity (R-D6).
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs
/// (HandleSignInAsync: RenewAsync when a session key was read, StoreAsync when not).
///
/// Only the cookie handler uses this manager. The OpenID Connect handler reads its correlation and
/// nonce cookies from the request directly, so the callback still sees those.
/// </summary>
internal sealed class CallbackBlindCookieManager(PathString callbackPath) : ICookieManager
{
    private readonly ChunkingCookieManager inner = new();

    /// <summary>
    /// Null on the callback path. <see cref="PathString"/> equality ignores case, as the OpenID Connect
    /// handler's own match of its callback path does, so the two agree on what the callback is.
    /// </summary>
    public string? GetRequestCookie(HttpContext context, string key) =>
        context.Request.Path == callbackPath ? null : inner.GetRequestCookie(context, key);

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        inner.AppendResponseCookie(context, key, value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        inner.DeleteCookie(context, key, options);
}
