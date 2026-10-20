namespace TestIdp;

/// <summary>Which logout the discovery document advertises. Both endpoints are always served.</summary>
public enum EndSessionStyle
{
    /// <summary>Discovery lists <c>end_session_endpoint</c>, and RP-initiated logout takes
    /// <c>id_token_hint</c>, <c>post_logout_redirect_uri</c>, <c>client_id</c> and <c>state</c>.</summary>
    Standard,

    /// <summary>Discovery lists no <c>end_session_endpoint</c>, the way Cognito's does not. Logout is
    /// <c>GET /logout?client_id=…&amp;logout_uri=…</c>, which is the difference ADR-0011 puts behind
    /// <c>IIdpProfile</c>.</summary>
    Cognito,
}

/// <summary>How a client authenticated itself on a back-channel request.</summary>
public enum ClientAuth
{
    /// <summary>No client credentials were presented.</summary>
    None,

    /// <summary><c>client_secret_basic</c>: the <c>Authorization</c> header.</summary>
    Basic,

    /// <summary><c>client_secret_post</c>: the form body.</summary>
    Post,
}

/// <summary>A registered OAuth client. Redirect and logout URIs are matched exactly.</summary>
public sealed record TestIdpClient(string ClientId, string Secret, IReadOnlySet<string> RedirectUris, IReadOnlySet<string> PostLogoutUris)
{
    /// <summary>A client with one redirect URI and one post-logout URI.</summary>
    public static TestIdpClient Create(string clientId, string secret, string redirectUri, string postLogoutUri)
        => new(clientId, secret, new HashSet<string>(StringComparer.Ordinal) { redirectUri },
            new HashSet<string>(StringComparer.Ordinal) { postLogoutUri });

    /// <summary>A client with one redirect URI and several post-logout URIs, compared ordinally as the one-URI form is.</summary>
    public static TestIdpClient Create(string clientId, string secret, string redirectUri, IEnumerable<string> postLogoutUris)
        => new(clientId, secret, new HashSet<string>(StringComparer.Ordinal) { redirectUri },
            new HashSet<string>(postLogoutUris, StringComparer.Ordinal));
}

/// <summary>One answered request at the authorize endpoint. It holds no <c>state</c> and no code.</summary>
public sealed record Authorization(int Seq, string ClientId, string RedirectUri, string Scope, string? Resource, string Subject, string Outcome)
{
    /// <summary>Whether the request was answered with a code.</summary>
    public bool Ok => Outcome == "ok";
}

/// <summary>One answered request at the token endpoint.</summary>
/// <param name="Seq">Position in the log, from 1.</param>
/// <param name="GrantType">The <c>grant_type</c> the request named.</param>
/// <param name="ClientId">The client that authenticated, or the empty string when none did.</param>
/// <param name="Auth">How the client authenticated.</param>
/// <param name="Outcome"><c>ok</c>, an OAuth error code, or <c>http_{status}</c> for an outage.</param>
/// <param name="RefreshTokenUsed">The refresh token presented, when the request was a refresh grant.</param>
/// <param name="RefreshTokenIssued">The refresh token the answer carried, when it carried one.</param>
/// <param name="AccessToken">The access token the answer carried, when it carried one.</param>
/// <param name="SentResource">Whether the request named <c>resource</c>.</param>
/// <param name="SentScope">Whether the request named <c>scope</c>.</param>
/// <param name="ReplayedInsideGrace">Whether a rotated-out refresh token was granted inside the window.</param>
public sealed record TokenGrant(
    int Seq, string GrantType, string ClientId, ClientAuth Auth, string Outcome,
    string? RefreshTokenUsed, string? RefreshTokenIssued, string? AccessToken,
    bool SentResource, bool SentScope, bool ReplayedInsideGrace)
{
    /// <summary>Whether the grant was carried out.</summary>
    public bool Ok => Outcome == "ok";

    /// <summary>A line for test output that names no token value.</summary>
    public override string ToString()
        => $"#{Seq} {GrantType} by {(ClientId.Length == 0 ? "(no client)" : ClientId)} ({Auth.ToString().ToLowerInvariant()})"
         + $" with {TestIdentityProvider.Label(RefreshTokenUsed)} -> {Outcome}"
         + (Ok ? $": {TestIdentityProvider.Label(AccessToken)}, {TestIdentityProvider.Label(RefreshTokenIssued)}" : "")
         + (ReplayedInsideGrace ? " [replayed inside the grace window]" : "");
}

/// <summary>One answered request at the revocation endpoint.</summary>
/// <param name="Seq">Position in the log, from 1.</param>
/// <param name="ClientId">The client that authenticated.</param>
/// <param name="Auth">How the client authenticated.</param>
/// <param name="Token">The token presented.</param>
/// <param name="Hint">The <c>token_type_hint</c>, when one was sent.</param>
/// <param name="Known">Whether the token was one this client holds.</param>
/// <param name="Revoked">How many refresh tokens the request revoked.</param>
public sealed record Revocation(int Seq, string ClientId, ClientAuth Auth, string Token, string? Hint, bool Known, int Revoked)
{
    /// <summary>A line for test output that names no token value.</summary>
    public override string ToString()
        => $"#{Seq} revoke {TestIdentityProvider.Label(Token)} by {ClientId} ({Auth.ToString().ToLowerInvariant()})"
         + $" -> {(Known ? "known" : "unknown")}, {Revoked} revoked";
}

/// <summary>One answered request at either logout endpoint.</summary>
/// <param name="Seq">Position in the log, from 1.</param>
/// <param name="Style">Which endpoint answered it.</param>
/// <param name="ClientId">The client the request named or the ID token hint identified.</param>
/// <param name="ReturnUri">The URI the browser was sent back to, when it was accepted.</param>
/// <param name="Outcome"><c>ok</c> or why it was refused.</param>
public sealed record Logout(int Seq, EndSessionStyle Style, string? ClientId, string? ReturnUri, string Outcome)
{
    /// <summary>Whether the request was accepted.</summary>
    public bool Ok => Outcome == "ok";
}
