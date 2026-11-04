using System.Globalization;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The access token for this request's session, refreshed when it is within a minute of expiry
/// (plan row 3.8, ADR-0005, ADR-0017).
///
/// The gateway owns the orchestration and the library owns the protocol. Under the session's gate this
/// takes the session from the store, not from the ticket the request authenticated with, decides whether
/// a refresh is due, asks the library's endpoint for it, and writes the new tokens back into the same
/// record (T-D1, T-D2). <see cref="AppSessions"/> enters the gate, checks the record is this app's and
/// this client's, makes the versioned write and the removal; every decision, and every line logged
/// about one, stays here. The record keeps its key and its expiry, so the cookie issued at login still
/// identifies it, and no cookie is issued on the way (S-E2, T-B9).
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
///
/// <para><b>The refresh lease</b> (ADR-0024; change request P2-3, Decision items 3 to 5; #332 D2 and
/// rows F3 to F6). Two gateway tasks share no session gate, so a due token is refreshed only by the task
/// holding the session's refresh lease, taken under the gate before the provider is called and given
/// back in a <c>finally</c> on every outcome, fenced by its token. Holding it, the holder reads the
/// record again, since another task may have written it since the turn began, and then checks, on the
/// monotonic clock and timed from the acquire's send, that the acquire and the re-read together took
/// at most <see cref="AccessTokenSourceOptions.StoreDeadline"/>, which is what leaves at least the lease
/// budget (<see cref="AccessTokenSourceOptions.ReservedBudget"/>) of its lease for the call, the write
/// and skew; with less left, it gives the lease back and waits as a loser rather than call the provider
/// (D2; change request P2-3's second amendment, 2026-10-29, decision 1). A loser re-reads the record every
/// <see cref="LoserPoll"/> and asks for the lease again: a record gone answers
/// <see cref="AccessTokenResult.Unauthenticated"/>, a token no longer due is the answer, and a lease
/// that lapsed with the session unmoved is taken over and the refresh made (F3's recovery path). A host
/// that registers no <see cref="IRefreshLeases"/> runs one replica, where the gate is the whole refresh
/// gate (plan row 3.4).</para>
///
/// <para><b>Admission at shutdown</b> (P2-3's second amendment, decision 3; #560). Once the host's
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> is observed at admission, after the awaited
/// acquire and re-read and before the provider call, no provider refresh starts on either refresh path.
/// The refusal gives any acquired lease back and answers <see cref="AccessTokenResult.ProviderUnavailable"/>
/// with the session kept, which the next host's call refreshes (F3's bounded recovery). A refresh admitted
/// before the stop keeps its own provider deadline and writes with no shutdown cancellation: the token
/// is read once, at admission, and reaches no deadline and no store call, so the durable write is what
/// the shutdown bound of decision 2 promises.</para>
///
/// <para>What the lease does not give: an exactly-once provider call. A holder paused between the check
/// and the send, past its lease, sends a refresh another task may also be sending; the grace window, the
/// versioned replace and the re-read before a refusal then decide, and at worst the session ends once
/// under one logged reason (F4). Nor does a release that fails, or a write that fails after the provider
/// rotated: the lease is released or lapses, and the next caller's read decides (F3b).</para>
/// </summary>
internal sealed partial class AccessTokenSource(
    AppSessions sessions,
    ProfileTokenEndpoints endpoints,
    CredentialStyles styles,
    AccessTokenSourceOptions options,
    TimeProvider clock,
    ILogger<AccessTokenSource> logger,
    IHostApplicationLifetime lifetime,
    IRefreshLeases? leases = null) : IAccessTokenSource
{
    /// <summary>
    /// How often a loser reads the record again and asks for the lease (change request P2-3, Decision
    /// item 4).
    /// </summary>
    internal static readonly TimeSpan LoserPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The lease's time to live, refused at construction when no holder could ever pass D2's check.</summary>
    private readonly TimeSpan timeToLive = options.Validated().LeaseTimeToLive;

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
        // none at all resolves to no session key. KeyOf notes a dead cookie as it answers, because the
        // proxy only receives Unauthenticated and cannot recover the distinction (telemetry.md).
        AuthenticateResult authenticated = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (sessions.KeyOf(context, authenticated) is not { } key)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        using AppSessions.Turn turn = await sessions.EnterAsync(app, key);

        switch (turn.WhileWaiting)
        {
            case RefreshOutcome.Unavailable:
                return new AccessTokenResult.ProviderUnavailable();
            case RefreshOutcome.Ended:
                return new AccessTokenResult.SessionEnded();
        }

        if (turn.Owned is not { } record)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        if (FreshAccess(record) is { } access)
        {
            return new AccessTokenResult.Token(access);
        }

        if (leases is null)
        {
            return (await RefreshAsync(context, app, key, turn, record)).Answer;
        }

        return await UnderTheLeaseAsync(context, app, key, turn, leases);
    }

    /// <summary>
    /// The holder's and the loser's loop (change request P2-3, Decision item 4). Each pass asks for the
    /// lease; holding it, reads the record and refreshes when the check allows; otherwise waits
    /// <see cref="LoserPoll"/> and reads the record again. Every store call here runs with
    /// <see cref="CancellationToken.None"/> (T-B15, T-B16).
    /// </summary>
    private async Task<AccessTokenResult> UnderTheLeaseAsync(
        HttpContext context, AppDefinition app, string key, AppSessions.Turn turn, IRefreshLeases table)
    {
        long arrived = clock.GetTimestamp();
        while (true)
        {
            // The clock starts before the request is sent: what the check charges against the lease is
            // the acquire and the re-read under it together, timed from here (#332 D2; change request
            // P2-3's second amendment).
            long asked = clock.GetTimestamp();
            if (await table.TryAcquireAsync(key, timeToLive, CancellationToken.None) is { } lease)
            {
                // "store_failed" is the label for the one way out not written below: a store call that
                // threw, the F3b and F17 path, whose exception reaches the caller after the release.
                string outcome = "store_failed";
                try
                {
                    if (await turn.RereadAsync() is not { } current)
                    {
                        outcome = "gone";
                        return new AccessTokenResult.Unauthenticated();
                    }

                    if (FreshAccess(current) is { } written)
                    {
                        // Another task refreshed between this turn's read and the lease: its token is good.
                        outcome = "unchanged";
                        turn.Finish(new RefreshOutcome.Refreshed());
                        return new AccessTokenResult.Token(written);
                    }

                    if (options.MayCallProvider(clock.GetElapsedTime(asked)))
                    {
                        (AccessTokenResult answer, RefreshOutcome ended) = await RefreshAsync(context, app, key, turn, current);
                        outcome = OutcomeName(ended);
                        return answer;
                    }

                    outcome = "too_little_lease";
                    TooLittleLease(logger, app.AppId, SessionRef.Of(key));
                }
                finally
                {
                    await ReleaseAsync(table, app, key, lease, outcome);
                }
            }

            if (clock.GetElapsedTime(arrived) > timeToLive + timeToLive)
            {
                // No honest holder keeps the lease this long: every lease lapses within one time to live,
                // and the next pass takes it over. Only a store too slow for any holder to pass the check
                // gets here, and it fails the call with the session kept, as an outage would.
                turn.Finish(new RefreshOutcome.Unavailable());
                NoLease(logger, app.AppId, SessionRef.Of(key));
                return new AccessTokenResult.ProviderUnavailable();
            }

            await Task.Delay(LoserPoll, clock);
            if (await turn.RereadAsync() is not { } reread)
            {
                return new AccessTokenResult.Unauthenticated();
            }

            if (FreshAccess(reread) is { } holders)
            {
                // The holder's refresh is written, and its token is the answer. Item 4 waits "until the
                // version moves"; a token no longer due is the stricter test, since a version that moved
                // without a usable token (another writer's record with none) leaves the loser waiting
                // and then refreshing, rather than answering with nothing.
                turn.Finish(new RefreshOutcome.Refreshed());
                return new AccessTokenResult.Token(holders);
            }
        }
    }

    /// <summary>
    /// The refresh itself, on the record this caller holds the gate (and the lease, when there is one)
    /// for, which is due: the library's call, then the write, the refusal's re-read or the end. Returns
    /// the answer and how the refresh ended, which the callers queued on the gate share (T-D6).
    /// </summary>
    private async Task<(AccessTokenResult Answer, RefreshOutcome Outcome)> RefreshAsync(
        HttpContext context, AppDefinition app, string key, AppSessions.Turn turn, SessionRecord record)
    {
        AuthenticationTicket ticket = Deserialize(record);
        if (ticket.Properties.GetTokenValue(RefreshTokenName) is not { Length: > 0 } refreshToken)
        {
            // Nothing to refresh with, so the session can never yield a token again.
            await EndAsync(context, app, turn);
            return Finished(turn, new AccessTokenResult.SessionEnded(), new RefreshOutcome.Ended());
        }

        // Admission (P2-3's second amendment, decision 3): read once, here, after the acquire and the
        // re-read on the lease path and after the gated read on the other, and never passed on. A stop
        // observed now refuses the refresh with the session kept; one observed a moment later finds a
        // refresh in flight, which finishes and writes under its own deadline. The callers queued on the
        // gate share the refusal (T-D6), and the lease path's finally gives the lease back.
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            RefusedAtStop(logger, app.AppId, SessionRef.Of(key));
            return Finished(turn, new AccessTokenResult.ProviderUnavailable(), new RefreshOutcome.Unavailable());
        }

        (UserToken? token, string? error) = await CallProviderAsync(app, refreshToken);

        if (token is not null)
        {
            turn.Finish(new RefreshOutcome.Refreshed());
            Refreshed(logger, app.AppId, SessionRef.Of(key));
            return (await StoreAsync(app, key, turn, record, ticket, token), new RefreshOutcome.Refreshed());
        }

        if (error != OidcConstants.TokenErrors.InvalidGrant)
        {
            RefreshFailed(logger, app.AppId, SessionRef.Of(key), error!);
            return Finished(turn, new AccessTokenResult.ProviderUnavailable(), new RefreshOutcome.Unavailable());
        }

        // Refused. Another writer may have refreshed the session in between, so it rotated the refresh
        // token this call presented: then the record holds its successor, and its access token is good.
        if (await sessions.ReadAsync(app, key, CancellationToken.None) is { } reread
            && Deserialize(reread).Properties is var current
            && current.GetTokenValue(RefreshTokenName) is { Length: > 0 } newer
            && !string.Equals(newer, refreshToken, StringComparison.Ordinal)
            && current.GetTokenValue(AccessTokenName) is { Length: > 0 } newerAccess)
        {
            return Finished(turn, new AccessTokenResult.Token(newerAccess), new RefreshOutcome.Refreshed());
        }

        await EndAsync(context, app, turn);
        Ended(logger, app.AppId, SessionRef.Of(key));
        return Finished(turn, new AccessTokenResult.SessionEnded(), new RefreshOutcome.Ended());
    }

    private static (AccessTokenResult, RefreshOutcome) Finished(AppSessions.Turn turn, AccessTokenResult answer, RefreshOutcome outcome)
    {
        turn.Finish(outcome);
        return (answer, outcome);
    }

    /// <summary>
    /// Gives the lease back, fenced by its token. A failure is logged by outcome, without the token or the
    /// key, and never reaches the caller: the lease lapses on its own (change request P2-3, Decision item 5).
    /// Only the exception's type is logged, since a store's message is text this code does not control.
    /// </summary>
    private async Task ReleaseAsync(IRefreshLeases table, AppDefinition app, string key, RefreshLease lease, string outcome)
    {
        try
        {
            await table.ReleaseAsync(key, lease, CancellationToken.None);
        }
        catch (Exception e)
        {
            ReleaseFailed(logger, app.AppId, SessionRef.Of(key), outcome, e.GetType().Name);
        }
    }

    private static string OutcomeName(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.Refreshed => "refreshed",
        RefreshOutcome.Unavailable => "unavailable",
        RefreshOutcome.Ended => "ended",
        _ => "unknown",
    };

    /// <summary>The record's access token when it is not due, else null (plan row 3.8's one-minute margin).</summary>
    private string? FreshAccess(SessionRecord record)
    {
        AuthenticationProperties properties = Deserialize(record).Properties;
        return properties.GetTokenValue(AccessTokenName) is { Length: > 0 } access
            && ExpiresAt(properties) > clock.GetUtcNow() + RefreshBeforeExpiry
                ? access
                : null;
    }

    /// <summary>
    /// The library's refresh call, with the app's two schemes and the issuer's credential style, under
    /// a deadline tied to no request. Returns the new tokens, or the failure as a fixed category.
    /// </summary>
    private async Task<(UserToken? Token, string? Error)> CallProviderAsync(AppDefinition app, string refreshToken)
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
        AppDefinition app, string key, AppSessions.Turn turn, SessionRecord record, AuthenticationTicket ticket, UserToken token)
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
        if (await turn.ReplaceAsync(next))
        {
            return new AccessTokenResult.Token(access);
        }

        // A concurrent writer won the versioned replace (T-D2). Return its persisted token rather
        // than this response's unpersisted token; a removed or rebound session authenticates nobody.
        if (await sessions.ReadAsync(app, key, CancellationToken.None) is not { } winner)
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
    private static async Task EndAsync(HttpContext context, AppDefinition app, AppSessions.Turn turn)
    {
        await turn.EndAsync();
        if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            await context.SignOutAsync(SchemeNames.Cookie(app.AppId));
        }
    }

    /// <summary>
    /// The provider's error as a fixed category. An OAuth error value is provider-controlled text that can
    /// carry a credential, so only known categories reach the warning (contracts/telemetry.md, N-16),
    /// <c>invalid_grant</c> among them because it alone ends a session (ADR-0005). Unknown text becomes
    /// <c>refresh_failed</c>.
    ///
    /// The OAuth error names are the library's constants, not literals: RFC 6749's vocabulary is the
    /// library's to spell, and one of them, <c>invalid_request</c>, is also a problem code in
    /// contracts/http-contract.md -> Errors, which no workstream project spells (#142, #158).
    /// </summary>
    private static string SafeError(string? error) => error switch
    {
        OidcConstants.TokenErrors.InvalidGrant or OidcConstants.TokenErrors.InvalidClient
            or OidcConstants.TokenErrors.InvalidRequest or OidcConstants.TokenErrors.InvalidScope
            or OidcConstants.TokenErrors.UnauthorizedClient or OidcConstants.TokenErrors.UnsupportedGrantType
            or OidcConstants.AuthorizeErrors.ServerError or OidcConstants.AuthorizeErrors.TemporarilyUnavailable
            or "deadline_exceeded" or "unreachable" => error,
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Too little of the refresh lease for app {AppId}, session {SessionRef} remained to call the identity provider. It was given back, and the call waits for another holder's refresh.")]
    private static partial void TooLittleLease(ILogger logger, string appId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The refresh lease for app {AppId}, session {SessionRef} was not given back after the refresh ended ({Outcome}): {ErrorType}. It lapses on its own.")]
    private static partial void ReleaseFailed(ILogger logger, string appId, string sessionRef, string outcome, string errorType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No refresh lease for app {AppId}, session {SessionRef} could be held long enough to refresh, within two times to live. The session is kept.")]
    private static partial void NoLease(ILogger logger, string appId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The host is stopping, so no refresh was started for app {AppId}, session {SessionRef}. The session is kept.")]
    private static partial void RefusedAtStop(ILogger logger, string appId, string sessionRef);
}

/// <summary>Settings for <see cref="AccessTokenSource"/>.</summary>
internal sealed class AccessTokenSourceOptions
{
    /// <summary>
    /// The clock-skew allowance both figures of #332 G1 add: the "+ 1 s" of
    /// <see cref="LeaseMargin"/>'s default and of <see cref="ReservedBudget"/>.
    /// </summary>
    internal static readonly TimeSpan Allowance = TimeSpan.FromSeconds(1);

    private TimeSpan? leaseMargin;

    /// <summary>
    /// How long a refresh may take before the call gives up and the session is kept (T-D9).
    /// A deadline cannot guarantee the next request arrives inside the provider's rotation grace:
    /// late retries can end the session, as T-D10 records. This is a bound on this call only, and on
    /// the OpenID Connect handler's code exchange, as its backchannel's timeout (#332 F1).
    /// </summary>
    public TimeSpan ProviderDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The bound on one session-store operation, its re-read and its one retry together: 5 s (#332 D4).
    /// Taking the lease is one such operation, and so is the versioned write after the refresh. One
    /// figure with the DynamoDB table's: its default is <see cref="DynamoDbSessionTable.DefaultStoreDeadline"/>,
    /// and <see cref="DynamoDbSessionTable.FromConfiguration"/> bounds the table's calls by this value, so
    /// the lease's margin always covers the store's real bound.
    /// </summary>
    public TimeSpan StoreDeadline { get; set; } = DynamoDbSessionTable.DefaultStoreDeadline;

    /// <summary>
    /// What the lease holds beyond <see cref="ProviderDeadline"/>: by default <c>2 × StoreDeadline + 1 s</c>,
    /// 11 s today. One <see cref="StoreDeadline"/> covers the acquire and the re-read under the lease
    /// together, the other the versioned write after the refresh, and the second is clock skew between
    /// tasks (change request P2-3, item 3 as amended 2026-10-24, and its second amendment of 2026-10-29;
    /// #332 G1). Set, it is that value instead.
    /// </summary>
    public TimeSpan LeaseMargin
    {
        get => leaseMargin ?? (StoreDeadline * 2) + Allowance;
        set => leaseMargin = value;
    }

    /// <summary>The lease's time to live, the one derived value: <c>ProviderDeadline + LeaseMargin</c>, 21 s today.</summary>
    public TimeSpan LeaseTimeToLive => ProviderDeadline + LeaseMargin;

    /// <summary>
    /// The lease budget: what must remain of the lease just before the provider is called,
    /// <c>ProviderDeadline + StoreDeadline + 1 s</c>, 16 s today, for the call, the versioned write after
    /// it and skew (#332 D2; change request P2-3's second amendment, 2026-10-29). "Lease budget" is this
    /// figure's name in prose and records; "the reserve" is <c>GLOSSARY.md</c>'s Reserve design, ADR-0017's
    /// locking, and is not this figure. The property keeps the name the amendment's table gives it.
    /// </summary>
    public TimeSpan ReservedBudget => ProviderDeadline + StoreDeadline + Allowance;

    /// <summary>
    /// D2's check: whether a holder whose lease was asked for <paramref name="sinceAsked"/> ago, on the
    /// monotonic clock, still has the lease budget, <see cref="ReservedBudget"/>, of it. It is timed from
    /// the acquire's send and read after the re-read under the lease, so the acquire and the re-read
    /// together may take at most <see cref="StoreDeadline"/> (<see cref="LeaseTimeToLive"/> less the lease
    /// budget): two 3 s operations fail it while each is inside its own deadline. "At least", so the
    /// boundary passes: the two together taking exactly <see cref="StoreDeadline"/> leaves exactly the
    /// lease budget (P2-3's second amendment, decision 1).
    /// </summary>
    public bool MayCallProvider(TimeSpan sinceAsked) => LeaseTimeToLive - sinceAsked >= ReservedBudget;

    /// <summary>
    /// These settings, refused when no holder could ever pass <see cref="MayCallProvider"/>: a time to live
    /// shorter than the lease budget would release and retake the lease until the loser's bound, and fail
    /// every refresh. It is what the 15 s lease did before #332 G1.
    /// </summary>
    public AccessTokenSourceOptions Validated()
    {
        if (ProviderDeadline <= TimeSpan.Zero || StoreDeadline <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The provider and store deadlines must be positive.");
        }

        if (LeaseTimeToLive < ReservedBudget)
        {
            throw new InvalidOperationException(
                $"The refresh lease's time to live ({LeaseTimeToLive.TotalSeconds} s) is shorter than the budget a holder must have left to call the provider ({ReservedBudget.TotalSeconds} s), so no refresh could ever be made.");
        }

        return this;
    }
}
