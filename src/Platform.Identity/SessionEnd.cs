using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The gateway's half of logout (contracts/http-contract.md -> <c>POST /bff/logout</c>, ADR-0017
/// decision 4): attempt to revoke the session's refresh token, then remove the session whatever the
/// provider said.
///
/// Under the session's gate (<see cref="AppSessions"/>), so a refresh in flight finishes first and the
/// token revoked is the one the record holds now, which may be newer than the one this request's ticket
/// carried. The revocation is made with the token library's parts (see <see cref="RevokeAsync"/>) in the
/// credential style of the app's issuer, the style Cognito's revocation endpoint insists on (C-2.19, C-2.21), for the app's own
/// scheme, under the provider deadline and never the request's cancellation token:
/// a browser that leaves mid-logout must not stop the session being removed. How the attempt ended is
/// logged by outcome only; the token itself reaches no log line.
///
/// The F11 exception (#386, F11 on #332): contracts/http-contract.md says logout "always removes the
/// session", and a remove the store cannot confirm by <c>StoreDeadline</c> is the one case it cannot
/// show that. The store throws <see cref="SessionStoreUnavailableException"/> for it, and the end
/// catches that here, for the remove alone, logs one line saying the removal is not confirmed, and lets
/// the logout go on as usual: both cookies cleared and the provider's logout, so nothing on the wire
/// changes (#386 Q187, Q191). A store read that fails before the remove, the cookie handler's or the
/// one under the gate, is not caught and keeps F17's 500 (#386 Q188). The end then marks the request's
/// session ended (<see cref="AppSessions.MarkEnded"/>), so the cookie handler's sign-out makes no store
/// call of its own (#386 Q192).
/// </summary>
internal sealed partial class SessionEnd(
    AppSessions sessions,
    IOpenIdConnectConfigurationService configurations,
    CredentialStyles styles,
    AccessTokenSourceOptions options,
    ILogger<SessionEnd> logger)
{
    private const string RefreshTokenName = "refresh_token";

    public async Task EndAsync(HttpContext context, AppDefinition app, string key)
    {
        // Entered outside the catch below: a read under the gate that fails keeps F17's 500 (#386 Q188).
        using AppSessions.Turn turn = await sessions.EnterAsync(app, key);

        // A record that is gone, or is not this app's and this client's, is owned by no turn and left
        // alone: the cookie handler's sign-out removes only a session the app owns
        // (AppSessions.RemoveAsync, through the ticket store), and so does this. The revocation is the
        // end's action, so the removal runs whatever it did.
        try
        {
            await turn.EndAsync(async record =>
            {
                string? refreshToken = TicketSerializer.Default.Deserialize(record.Ticket.ToArray())?
                    .Properties.GetTokenValue(RefreshTokenName);
                string outcome = refreshToken is { Length: > 0 } ? await RevokeAsync(app, refreshToken) : "no_refresh_token";
                Revocation(logger, app.AppId, SessionRef.Of(key), outcome);
            });
        }
        catch (SessionStoreUnavailableException e)
        {
            // The F11 exception (#386): the revocation touches no store, so inside this try only the
            // remove can throw this. Its message and inner exception stay off the line, which names the
            // operation and the session's reference and nothing else (N-16).
            RemovalNotConfirmed(logger, app.AppId, SessionRef.Of(key), e.Operation);
        }

        sessions.MarkEnded(context, key);
    }

    /// <summary>
    /// The revocation, and how the provider answered it, as a word fit for a log line: <c>revoked</c>, or
    /// why not. The token library's own revoke call answers nothing (4.2.0: it returns no result and only
    /// logs a failure), and ADR-0017 decision 4 asks for the provider's answer to be checked, so the call
    /// is put together from the library's parts instead: its configuration service supplies the
    /// endpoint from discovery, the client and the backchannel for the app's scheme, and its protocol
    /// client (<c>Duende.IdentityModel</c>'s <c>RevokeTokenAsync</c>, the call the library makes itself)
    /// sends the request and reads the answer. Nothing of the protocol is written here.
    /// </summary>
    private async Task<string> RevokeAsync(AppDefinition app, string refreshToken)
    {
        using CancellationTokenSource deadline = new(options.ProviderDeadline);
        try
        {
            OpenIdConnectClientConfiguration client = await configurations.GetOpenIdConnectConfigurationAsync(
                Scheme.Parse(SchemeNames.Oidc(app.AppId)), deadline.Token);
            if (client.RevocationEndpoint is not { } endpoint || client.HttpClient is not { } backchannel)
            {
                return "no_revocation_endpoint";
            }

            using TokenRevocationRequest request = new()
            {
                Address = endpoint.ToString(),
                ClientId = client.ClientId.ToString(),
                ClientSecret = client.ClientSecret?.ToString(),
                ClientCredentialStyle = ProfileTokenEndpoints.LibraryStyle(styles.Of(app)),
                Token = refreshToken,
                TokenTypeHint = OidcConstants.TokenTypes.RefreshToken,
            };
            TokenRevocationResponse response = await backchannel.RevokeTokenAsync(request, deadline.Token);
            return !response.IsError ? "revoked"
                : response.HttpStatusCode != 0 ? "http_" + (int)response.HttpStatusCode
                : "unreachable";
        }
        catch (OperationCanceledException)
        {
            return "deadline_exceeded";
        }
        catch (HttpRequestException e)
        {
            return e.StatusCode is { } status ? "http_" + (int)status : "unreachable";
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Logout for app {AppId} is removing session {SessionRef}; revoking its refresh token: {Outcome}.")]
    private static partial void Revocation(ILogger logger, string appId, string sessionRef, string outcome);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Logout for app {AppId}: removal of session {SessionRef} not confirmed; the session store's {Operation} "
            + "did not complete by StoreDeadline. The logout goes on and clears the cookies (F11).")]
    private static partial void RemovalNotConfirmed(ILogger logger, string appId, string sessionRef, string operation);
}
