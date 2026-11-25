using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// What the proxy got when it asked for an access token. Source: docs/plan/02-contracts.md ->
/// Interfaces. Assigned to G1b (ADR-0017); the contracts-v1 tag records the freeze.
/// </summary>
public abstract record AccessTokenResult
{
    /// <summary>A usable access token.</summary>
    public sealed record Token(string Value) : AccessTokenResult
    {
        /// <summary>Kept out of any string a log or an exception could carry.</summary>
        public override string ToString() => nameof(Token);
    }

    /// <summary>No session, so no token. The proxy answers 401.</summary>
    public sealed record Unauthenticated : AccessTokenResult;

    /// <summary>Refresh failed with <c>invalid_grant</c> and the session was removed (task 3.8).</summary>
    public sealed record SessionEnded : AccessTokenResult;

    /// <summary>
    /// The provider could not be reached, or failed the refresh other than with <c>invalid_grant</c>; or
    /// no refresh was started because the host is stopping (change request P2-3's second amendment,
    /// decision 3); or another writer replaced the session during the refresh and left it with no access
    /// token. The session is kept. The proxy answers 503 <c>idp_unavailable</c>, which is not a rejection
    /// and carries no rule id. ADR-0005 and evidence T-A9 and T-A15 named this outcome; change request 4.2
    /// added it after the G1b freeze, as a new type that changes no existing signature.
    /// </summary>
    public sealed record ProviderUnavailable : AccessTokenResult;

    /// <summary>
    /// A session store operation failed or ran out <c>StoreDeadline</c>, or a refresh ended at the
    /// store's bound, two times to live after it began without a grant (the <c>continuity</c> spec's
    /// F17 and D2). No cookie is cleared and the session, if it exists, is kept. The source notes
    /// <c>auth.store</c> before it returns; the proxy answers 503 <c>session_store_unavailable</c> and
    /// writes no rule of its own. Change request #729 added it between gates, as a new type that
    /// changes no existing signature.
    /// </summary>
    public sealed record StoreUnavailable : AccessTokenResult;

    /// <summary>
    /// The request carries <see cref="IFragmentFeature"/> and the session holds no usable token set
    /// for that fragment: none was acquired (the fragment was onboarded after this login, or its hop
    /// never returned), its set ended on <c>invalid_grant</c>, or the set's recorded <c>clientId</c> is
    /// no longer the fragment's. The session is kept, and the shell's set is never returned in its
    /// place. The proxy answers 401 <c>fragment_login_required</c>, which is not a rejection and
    /// carries no rule id, like the live-session 401. With no session at all the source returns
    /// <see cref="Unauthenticated"/>. The fragments spec (#961) added it with G6's tag, as a new type
    /// that changes no existing signature.
    /// </summary>
    public sealed record FragmentLoginRequired : AccessTokenResult;
}

/// <summary>
/// The access token for this request's session, refreshed if it is close to expiry: the shell's token
/// set, or, when the request carries <see cref="IFragmentFeature"/>, that fragment's set and never the
/// shell's. Implemented over Duende.AccessTokenManagement (ADR-0005, task 3.8); only
/// <c>Platform.Identity</c> may reference that package (check A-04). Freezes at G1b; the fragments
/// spec (#961) keeps the signature and adds the fragment reading.
/// </summary>
public interface IAccessTokenSource
{
    ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct);
}
