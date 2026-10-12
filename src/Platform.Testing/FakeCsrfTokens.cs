using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="ICsrfTokens"/> that issues a token bound to the app and checks it off the
/// <c>X-CSRF-TOKEN</c> header. It never reads the request body, which is the whole point of the
/// header-only rule (finding R-E7).
///
/// Set <see cref="Force"/> to accept or refuse everything, for a test that is exercising something
/// else behind the check. Workstream 4 owns the real implementation (task 4.4) and N-06 is proved
/// against that, not against this.
/// </summary>
public sealed class FakeCsrfTokens : ICsrfTokens
{
    public const string CookieName = "__Host-spa-csrf";
    public const string HeaderName = "X-CSRF-TOKEN";

    private readonly HashSet<string> issued = new(StringComparer.Ordinal);

    /// <summary>Accept every request (true) or refuse every request (false). Null checks the token.</summary>
    public bool? Force { get; set; }

    public string Issue(HttpContext context)
    {
        string token = TokenFor(context);
        issued.Add(token);

        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });

        return token;
    }

    public Task<bool> IsValidAsync(HttpContext context)
    {
        if (Force is bool forced)
        {
            return Task.FromResult(forced);
        }

        string? presented = context.Request.Headers[HeaderName];

        // Header only. Nothing here touches context.Request.Body.
        bool valid = presented is not null
            && issued.Contains(presented)
            && string.Equals(presented, TokenFor(context), StringComparison.Ordinal);

        return Task.FromResult(valid);
    }

    public void Clear(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });

    /// <summary>
    /// The app id is bound into the token, so a token issued for one app does not validate on
    /// another (R-E6).
    /// </summary>
    private static string TokenFor(HttpContext context)
    {
        string appId = context.Features.Get<IAppFeature>()?.App.AppId ?? "no-app";
        return "csrf-for-" + appId;
    }
}
