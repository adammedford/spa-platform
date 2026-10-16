using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// What the proxy got when it asked for an access token. Source: docs/plan/02-contracts.md ->
/// Interfaces. Assigned to G1b (ADR-0017); the contracts-v1 tag records the freeze.
/// </summary>
public abstract record AccessTokenResult
{
    /// <summary>A usable access token.</summary>
    public sealed record Token(string Value) : AccessTokenResult;

    /// <summary>No session, so no token. The proxy answers 401.</summary>
    public sealed record Unauthenticated : AccessTokenResult;

    /// <summary>Refresh failed with <c>invalid_grant</c> and the session was removed (task 3.8).</summary>
    public sealed record SessionEnded : AccessTokenResult;

    /// <summary>
    /// The provider could not be reached, or failed the refresh other than with <c>invalid_grant</c>.
    /// The session is kept. The proxy answers 503 <c>idp_unavailable</c>, which is not a rejection and
    /// carries no rule id. ADR-0005 and evidence T-A9 and T-A15 named this outcome; change request 4.2
    /// added it after the G1b freeze, as a new type that changes no existing signature.
    /// </summary>
    public sealed record ProviderUnavailable : AccessTokenResult;
}

/// <summary>
/// The access token for this request's session, refreshed if it is close to expiry. Implemented over
/// Duende.AccessTokenManagement (ADR-0005, task 3.8); only <c>Platform.Identity</c> may reference
/// that package (check A-04). Freezes at G1b.
/// </summary>
public interface IAccessTokenSource
{
    ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct);
}
