using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// The antiforgery token pair: a cookie token and a request token the SPA sends back in
/// <c>X-CSRF-TOKEN</c>. Header tokens only, so a request body is never read (task 4.4, finding
/// R-E7). Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a.
/// </summary>
public interface ICsrfTokens
{
    /// <summary>Sets the <c>__Host-spa-csrf</c> cookie and returns the request token to send back.</summary>
    string Issue(HttpContext context);

    /// <summary>
    /// Whether this request carries a valid token for this app. Must not read the request body.
    /// </summary>
    Task<bool> IsValidAsync(HttpContext context);

    /// <summary>Clears the cookie, at logout.</summary>
    void Clear(HttpContext context);
}
