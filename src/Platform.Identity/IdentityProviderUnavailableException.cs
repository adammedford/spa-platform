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
/// The message names the app and the provider's error code, never a token: the library reports a
/// failure as an error string (T-A8), which here is an OAuth error code or an HTTP status.
/// </summary>
public sealed class IdentityProviderUnavailableException : Exception
{
    public IdentityProviderUnavailableException(string appId, string error)
        : base($"The identity provider did not refresh the access token for app '{appId}' ({error}). The session is kept.")
    {
        AppId = appId;
        Error = error;
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

    /// <summary>The app whose token was due.</summary>
    public string AppId { get; }

    /// <summary>What the provider or the transport reported: an OAuth error code or a status, never a token.</summary>
    public string Error { get; }
}
