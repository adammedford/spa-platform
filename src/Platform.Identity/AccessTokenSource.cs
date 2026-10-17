using System.Globalization;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The access token for this request's session, refreshed when it is within a minute of expiry
/// (plan row 3.8, ADR-0005, ADR-0017).
///
/// The gateway owns the orchestration and the library owns the protocol. Under the session's gate
/// (<see cref="SessionGates"/>) this reads the session from the store, not from the ticket the request
/// authenticated with, decides whether a refresh is due, asks the library's endpoint for it, and writes
/// the new tokens back into the same record (T-D1, T-D2). The record keeps its key and its expiry, so
/// the cookie issued at login still identifies it, and no cookie is issued on the way (S-E2, T-B9).
///
/// Four rules from the gate runs hold here:
/// <list type="bullet">
/// <item>Every library call names both the app's challenge scheme and its sign-in scheme, and the
/// library's own default is the sentinel (<see cref="ProfileTokenEndpoints.SentinelScheme"/>, T-A1, T-A7).
/// No resource is named on the refresh: the audience survives it (T-B12, C-2.9).</item>
/// <item>Only <c>invalid_grant</c> is a refusal. The library reports a refusal, an outage and an
/// unreachable provider the same way, as a failed result (T-A8, T-A9, T-A15). A refusal ends the
/// session; anything else leaves it and fails the call with
/// <see cref="AccessTokenResult.ProviderUnavailable"/>, which the proxy answers 503
/// <c>idp_unavailable</c> (contracts/http-contract.md -> Errors, change request 4.2).</item>
/// <item>Before a refusal ends the session, the record is read again, and if it now holds a different
/// refresh token, another writer refreshed in between and its access token is used (T-B8).</item>
/// <item>The request's cancellation token reaches nothing here. A browser that disconnects mid-refresh
/// would otherwise strand the rotated refresh token at the provider (T-B15, T-B16). The refresh has a
/// deadline of its own instead (T-D9), with the caveat T-D10 records: a refresh given up on may still
/// have been carried out, and the retry is then a replay that only a grace window covers.</item>
/// </list>
/// </summary>
internal sealed partial class AccessTokenSource(
    ISessionStore sessions,
    SessionGates gates,
    ProfileTokenEndpoints endpoints,
    CredentialStyles styles,
    AccessTokenSourceOptions options,
    TimeProvider clock,
    ILogger<AccessTokenSource> logger) : IAccessTokenSource
{
    /// <summary>
    /// How long before expiry a token is refreshed: plan row 3.8, and the library's own default for
    /// <see cref="UserTokenManagementOptions.RefreshBeforeExpiration"/>.
    /// </summary>
    internal static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(1);

    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string ExpiresAtName = "expires_at";
    private const string IdTokenName = "id_token";

    /// <param name="context">The request, pinned to its app by host resolution.</param>
    /// <param name="ct">Not used, deliberately: see the class summary (T-B16).</param>
    public async ValueTask<AccessTokenResult> GetAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Features.Get<IAppFeature>()?.App is not { } app)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        // The authentication middleware has authenticated this scheme already; the handler answers
        // again from what it read. A cookie for no session, another app's session, an expired one or
        // none at all leaves no session key behind.
        AuthenticateResult authenticated = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (!authenticated.Succeeded || context.Features.Get<SessionKeyFeature>()?.Key is not { } key)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        using SessionGates.Lease lease = await gates.EnterAsync(key);

        switch (lease.WhileWaiting)
        {
            case RefreshOutcome.Unavailable:
                return new AccessTokenResult.ProviderUnavailable();
            case RefreshOutcome.Ended:
                return new AccessTokenResult.SessionEnded();
        }

        if (await OwnedAsync(app, key) is not { } record)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        AuthenticationTicket ticket = Deserialize(record);
        string? access = ticket.Properties.GetTokenValue(AccessTokenName);
        if (access is { Length: > 0 } && ExpiresAt(ticket.Properties) > clock.GetUtcNow() + RefreshBeforeExpiry)
        {
            return new AccessTokenResult.Token(access);
        }

        if (ticket.Properties.GetTokenValue(RefreshTokenName) is not { Length: > 0 } refreshToken)
        {
            // Nothing to refresh with, so the session can never yield a token again.
            await EndAsync(context, app, key);
            lease.Finish(new RefreshOutcome.Ended());
            return new AccessTokenResult.SessionEnded();
        }

        (UserToken? token, string? error) = await RefreshAsync(app, refreshToken);

        if (token is not null)
        {
            lease.Finish(new RefreshOutcome.Refreshed());
            Refreshed(logger, app.AppId, SessionRef.Of(key));
            return await StoreAsync(app, key, record, ticket, token);
        }

        if (error != "invalid_grant")
        {
            lease.Finish(new RefreshOutcome.Unavailable());
            RefreshFailed(logger, app.AppId, SessionRef.Of(key), error!);
            return new AccessTokenResult.ProviderUnavailable();
        }

        // Refused. Another writer may have refreshed the session in between, so it rotated the refresh
        // token this call presented: then the record holds its successor, and its access token is good.
        if (await OwnedAsync(app, key) is { } reread
            && Deserialize(reread).Properties is var current
            && current.GetTokenValue(RefreshTokenName) is { Length: > 0 } newer
            && !string.Equals(newer, refreshToken, StringComparison.Ordinal)
            && current.GetTokenValue(AccessTokenName) is { Length: > 0 } newerAccess)
        {
            lease.Finish(new RefreshOutcome.Refreshed());
            return new AccessTokenResult.Token(newerAccess);
        }

        await EndAsync(context, app, key);
        lease.Finish(new RefreshOutcome.Ended());
        Ended(logger, app.AppId, SessionRef.Of(key));
        return new AccessTokenResult.SessionEnded();
    }

    /// <summary>
    /// The library's refresh call, with the app's two schemes and the issuer's credential style, under
    /// a deadline tied to no request. Returns the new tokens, or the failure as a fixed category.
    /// </summary>
    private async Task<(UserToken? Token, string? Error)> RefreshAsync(AppDefinition app, string refreshToken)
    {
        UserTokenRequestParameters parameters = new()
        {
            ChallengeScheme = Scheme.Parse(SchemeNames.Oidc(app.AppId)),
            SignInScheme = Scheme.Parse(SchemeNames.Cookie(app.AppId)),
        };

        using CancellationTokenSource deadline = new(options.ProviderDeadline);
        try
        {
            TokenResult<UserToken> result = await endpoints.For(styles.Of(app)).RefreshAccessTokenAsync(
                new UserRefreshToken(RefreshToken.Parse(refreshToken), null), parameters, deadline.Token);

            return result.Succeeded
                ? (result.Token, null)
                : (null, SafeError(result.FailedResult.Error));
        }
        catch (OperationCanceledException)
        {
            // The deadline, or the shared backchannel's own timeout.
            return (null, "deadline_exceeded");
        }
        catch (HttpRequestException e)
        {
            return (null, e.StatusCode is { } status ? "http_" + (int)status : "unreachable");
        }
    }

    /// <summary>
    /// Writes the new tokens into the same record, one version on. The refresh token is replaced only
    /// when the provider rotated it, and the ID token only when the answer carried one (T-B13).
    /// </summary>
    private async Task<AccessTokenResult> StoreAsync(
        AppDefinition app, string key, SessionRecord record, AuthenticationTicket ticket, UserToken token)
    {
        string access = token.AccessToken.ToString();
        Dictionary<string, string> tokens = ticket.Properties.GetTokens()
            .ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
        tokens[AccessTokenName] = access;
        tokens[ExpiresAtName] = token.Expiration.ToString("o", CultureInfo.InvariantCulture);
        if (token.RefreshToken is { } rotated)
        {
            tokens[RefreshTokenName] = rotated.ToString();
        }

        if (token.IdentityToken is { } identity)
        {
            tokens[IdTokenName] = identity.ToString();
        }

        ticket.Properties.StoreTokens(tokens.Select(t => new AuthenticationToken { Name = t.Key, Value = t.Value }));

        SessionRecord next = record with { Version = record.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        if (await sessions.ReplaceAsync(key, next, record.Version, CancellationToken.None))
        {
            return new AccessTokenResult.Token(access);
        }

        // A concurrent writer won the versioned replace (T-D2). Return its persisted token rather
        // than this response's unpersisted token; a removed or rebound session authenticates nobody.
        if (await OwnedAsync(app, key) is not { } winner)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        if (Deserialize(winner).Properties.GetTokenValue(AccessTokenName) is { Length: > 0 } current)
        {
            return new AccessTokenResult.Token(current);
        }

        // The winner is a live session with no token to hand back. ProviderUnavailable is the one outcome
        // that keeps a session without a token (change request 4.2), and the next call, finding no access
        // token, tries a refresh with the winner's refresh token.
        LostToRecordWithoutToken(logger, app.AppId, SessionRef.Of(key));
        return new AccessTokenResult.ProviderUnavailable();
    }

    /// <summary>
    /// Removes the session, then clears the cookie. The record goes under the gate and with no
    /// cancellation, so a waiter can trust what it reads; the sign-out then expires the browser's cookie,
    /// unless the browser has already gone.
    /// </summary>
    private async Task EndAsync(HttpContext context, AppDefinition app, string key)
    {
        await sessions.RemoveAsync(key, CancellationToken.None);
        if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            await context.SignOutAsync(SchemeNames.Cookie(app.AppId));
        }
    }

    /// <summary>The record under the key, when it is still this app's and this client's (task 3.3, task 3.6).</summary>
    private async Task<SessionRecord?> OwnedAsync(AppDefinition app, string key) =>
        await sessions.GetAsync(key, CancellationToken.None) is { } record
        && string.Equals(record.AppId, app.AppId, StringComparison.Ordinal)
        && string.Equals(record.ClientId, app.OAuth.ClientId, StringComparison.Ordinal)
            ? record
            : null;

    /// <summary>
    /// The provider's error as a fixed category. An OAuth error value is provider-controlled text that can
    /// carry a credential, so only known categories reach the warning (contracts/telemetry.md, N-16),
    /// <c>invalid_grant</c> among them because it alone ends a session (ADR-0005). Unknown text becomes
    /// <c>refresh_failed</c>.
    /// </summary>
    private static string SafeError(string? error) => error switch
    {
        "invalid_grant" or "invalid_client" or "invalid_request" or "invalid_scope"
            or "unauthorized_client" or "unsupported_grant_type" or "server_error"
            or "temporarily_unavailable" or "deadline_exceeded" or "unreachable" => error,
        _ => "refresh_failed",
    };

    private static AuthenticationTicket Deserialize(SessionRecord record) =>
        TicketSerializer.Default.Deserialize(record.Ticket.ToArray())
        ?? throw new InvalidOperationException("A session record holds a serialized ticket.");

    /// <summary>
    /// When the access token expires. A token saved with no expiry is treated as not known to expire,
    /// as the library treats it.
    /// </summary>
    private static DateTimeOffset ExpiresAt(AuthenticationProperties properties) =>
        DateTimeOffset.TryParse(properties.GetTokenValue(ExpiresAtName), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? at
            : DateTimeOffset.MaxValue;

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed the access token for app {AppId}, session {SessionRef}.")]
    private static partial void Refreshed(ILogger logger, string appId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The identity provider did not refresh the access token for app {AppId}, session {SessionRef} ({Error}). The session is kept.")]
    private static partial void RefreshFailed(ILogger logger, string appId, string sessionRef, string error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The identity provider refused the refresh token for app {AppId}, so session {SessionRef} has ended.")]
    private static partial void Ended(ILogger logger, string appId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Another writer replaced the session for app {AppId}, session {SessionRef} during the refresh and left no access token in it. The session is kept.")]
    private static partial void LostToRecordWithoutToken(ILogger logger, string appId, string sessionRef);
}

/// <summary>Settings for <see cref="AccessTokenSource"/>.</summary>
internal sealed class AccessTokenSourceOptions
{
    /// <summary>
    /// How long a refresh may take before the call gives up and the session is kept (T-D9).
    /// A deadline cannot guarantee the next request arrives inside the provider's rotation grace:
    /// late retries can end the session, as T-D10 records. This is a bound on this call only.
    /// </summary>
    public TimeSpan ProviderDeadline { get; set; } = TimeSpan.FromSeconds(10);
}
