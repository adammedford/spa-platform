namespace Platform.Identity;

/// <summary>
/// The access token was due and the identity provider failed to refresh it other than by refusing the
/// refresh token: an outage, an unreachable provider, a refresh that missed its deadline, or any error
/// but <c>invalid_grant</c> (T-A8, T-A9, T-A15). The session is kept, and the next call tries again.
///
/// Task 3.8's row says such a failure "leaves the session and fails the call". The frozen
/// <c>AccessTokenResult</c> has no outcome for it, which change request
/// <c>contracts/changes/4.2-access-token-result-provider-unavailable.md</c> asks for; until it is
/// decided, failing the call is this exception.
///
/// The provider controls its error text, so only fixed outcome codes reach a message or log
/// (contracts/telemetry.md, N-16); unknown text becomes refresh_failed.
/// </summary>
public sealed class IdentityProviderUnavailableException : Exception
{
    public IdentityProviderUnavailableException(string appId, string error)
        : base($"The identity provider did not refresh the access token for app '{appId}' ({SafeError(error)}). The session is kept.")
    {
        AppId = appId;
        Error = SafeError(error);
    }

    public IdentityProviderUnavailableException()
    {
        AppId = string.Empty;
        Error = string.Empty;
    }

    public IdentityProviderUnavailableException(string message)
        : base(message)
    {
        AppId = string.Empty;
        Error = string.Empty;
    }

    public IdentityProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        AppId = string.Empty;
        Error = string.Empty;
    }

    // OAuth error values can carry arbitrary provider text. Preserve only known categories,
    // including invalid_grant, whose distinction determines whether the session ends (ADR-0005).
    internal static string SafeError(string? error) => error switch
    {
        "invalid_grant" or "invalid_client" or "invalid_request" or "invalid_scope"
            or "unauthorized_client" or "unsupported_grant_type" or "server_error"
            or "temporarily_unavailable" or "deadline_exceeded" or "unreachable" => error,
        _ => "refresh_failed",
    };

    /// <summary>The app whose token was due.</summary>
    public string AppId { get; }

    /// <summary>A fixed failure category, with arbitrary provider text discarded.</summary>
    public string Error { get; }
}
