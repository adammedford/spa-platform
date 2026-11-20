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

    /// <summary>
    /// How many versioned replaces a refresh's write makes at most, the first included (#995 -> "Writes
    /// after a lost versioned replace").
    /// </summary>
    internal const int WriteAttempts = 3;

    /// <param name="context">The request, pinned to its app by host resolution, and to a fragment on a fragment route.</param>
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

        // On a fragment route the answer is that fragment's set and never the shell's (#995): host
        // resolution pins the fragment, from the same snapshot as the app (contracts/pipeline.md).
        TokenSet set = context.Features.Get<IFragmentFeature>()?.Fragment is { } fragment ? TokenSet.Of(fragment) : TokenSet.Shell;
        try
        {
            return await ForSetAsync(context, new Refresh(app, key, set));
        }
        catch (SessionStoreUnavailableException e)
        {
            // auth.store (#727): a store call under the turn failed, whichever it was: the gated read, the
            // lease, a re-read, the versioned write or an end's remove. The session, if it exists, is kept,
            // no cookie is cleared, and the proxy's access-token step answers the result.
            StoreFailure.Note(context, e.Operation);
            return new AccessTokenResult.StoreUnavailable();
        }
    }

    /// <summary>The answer for one token set of the session, under that set's gate.</summary>
    private async Task<AccessTokenResult> ForSetAsync(HttpContext context, Refresh refresh)
    {
        using AppSessions.Turn turn = refresh.Set.Fragment is { } fragment
            ? await sessions.EnterAsync(context, refresh.App, refresh.Key, fragment)
            : await sessions.EnterAsync(refresh.App, refresh.Key);

        switch (turn.WhileWaiting)
        {
            case RefreshOutcome.Unavailable:
                return new AccessTokenResult.ProviderUnavailable();
            case RefreshOutcome.Ended:
                return refresh.EndedAnswer;
            case RefreshOutcome.AtStoreBound:
                // A waiter answers as the turn it inherits did; the line is that turn's (#727).
                Rejection.Note(context, RuleIds.AuthStore);
                return new AccessTokenResult.StoreUnavailable();
        }

        if (turn.Owned is not { } record)
        {
            return new AccessTokenResult.Unauthenticated();
        }

        if (FreshAccess(record, refresh.Set) is { } access)
        {
            return new AccessTokenResult.Token(access);
        }

        if (leases is null)
        {
            // The no-lease path counts the write deadline from the gated read (#995).
            return (await RefreshAsync(context, refresh, turn, record, clock.GetTimestamp())).Answer;
        }

        return await UnderTheLeaseAsync(context, refresh, turn, leases);
    }

    /// <summary>
    /// The holder's and the loser's loop (change request P2-3, Decision item 4), on the set's own lease
    /// key. Each pass asks for the lease; holding it, reads the record and refreshes when the check
    /// allows; otherwise waits <see cref="LoserPoll"/> and reads the record again. Every store call here
    /// runs with <see cref="CancellationToken.None"/> (T-B15, T-B16).
    /// </summary>
    private async Task<AccessTokenResult> UnderTheLeaseAsync(HttpContext context, Refresh refresh, AppSessions.Turn turn, IRefreshLeases table)
    {
        string leaseKey = refresh.Set.LeaseKey(refresh.Key);
        long arrived = clock.GetTimestamp();
        while (true)
        {
            // The clock starts before the request is sent: what the check charges against the lease is
            // the acquire and the re-read under it together, timed from here (#332 D2; change request
            // P2-3's second amendment). The refresh's write deadline counts from here too (#995).
            long asked = clock.GetTimestamp();
            if (await table.TryAcquireAsync(leaseKey, timeToLive, CancellationToken.None) is { } lease)
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

                    if (FreshAccess(current, refresh.Set) is { } written)
                    {
                        // Another task refreshed between this turn's read and the lease: its token is good.
                        outcome = "unchanged";
                        turn.Finish(new RefreshOutcome.Refreshed());
                        return new AccessTokenResult.Token(written);
                    }

                    if (options.MayCallProvider(clock.GetElapsedTime(asked)))
                    {
                        (AccessTokenResult answer, RefreshOutcome ended) = await RefreshAsync(context, refresh, turn, current, asked);
                        outcome = OutcomeName(ended);
                        return answer;
                    }

                    outcome = "too_little_lease";
                    LogTooLittleLease(refresh);
                }
                finally
                {
                    await ReleaseAsync(table, refresh, leaseKey, lease, outcome);
                }
            }

            if (clock.GetElapsedTime(arrived) > timeToLive + timeToLive)
            {
                // No honest holder keeps the lease this long: every lease lapses within one time to live,
                // and the next pass takes it over. Only a store too slow for any holder to pass the check
                // gets here, and it fails the call with the session kept: the store's bound, #332 D2's
                // "lease exhaustion", which answers under auth.store and not as a provider outage (#729's
                // decision, part 4; #727). Its own Warning line keeps the cause apart from a failed call.
                turn.Finish(new RefreshOutcome.AtStoreBound());
                LogNoLease(refresh);
                Rejection.Note(context, RuleIds.AuthStore);
                return new AccessTokenResult.StoreUnavailable();
            }

            await Task.Delay(LoserPoll, clock);
            if (await turn.RereadAsync() is not { } reread)
            {
                return new AccessTokenResult.Unauthenticated();
            }

            if (FreshAccess(reread, refresh.Set) is { } holders)
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
    /// The refresh itself, on the record this caller holds the set's gate (and the lease, when there is
    /// one) for, which is due: the library's call, then the write, the refusal's re-read or the end.
    /// Returns the answer and how the refresh ended, which the callers queued on the gate share (T-D6).
    /// </summary>
    /// <param name="writeFrom">The monotonic timestamp the write deadline counts from: the acquire's send, or the gated read.</param>
    private async Task<(AccessTokenResult Answer, RefreshOutcome Outcome)> RefreshAsync(
        HttpContext context, Refresh refresh, AppSessions.Turn turn, SessionRecord record, long writeFrom)
    {
        AuthenticationTicket ticket = Deserialize(record);
        if (refresh.Set.Token(ticket.Properties, TokenSet.RefreshTokenName) is not { } refreshToken)
        {
            if (refresh.Set.Fragment is not null)
            {
                // A fragment's set with nothing to refresh with ends nothing: the record and every other set
                // stay, and the call answers as for no usable set (#995 -> "Expiry").
                return (new AccessTokenResult.FragmentLoginRequired(), new RefreshOutcome.Unavailable());
            }

            // Nothing to refresh the shell's set with, so the session can never yield a token again.
            await EndAsync(context, refresh.App, turn);
            return Finished(turn, new AccessTokenResult.SessionEnded(), new RefreshOutcome.Ended());
        }

        // Admission (P2-3's second amendment, decision 3): read once, here, after the acquire and the
        // re-read on the lease path and after the gated read on the other, and never passed on. A stop
        // observed now refuses the refresh with the session kept; one observed a moment later finds a
        // refresh in flight, which finishes and writes under its own deadline. The callers queued on the
        // gate share the refusal (T-D6), and the lease path's finally gives the lease back.
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            RefusedAtStop(logger, refresh.App.AppId, SessionRef.Of(refresh.Key));
            return Finished(turn, new AccessTokenResult.ProviderUnavailable(), new RefreshOutcome.Unavailable());
        }

        (UserToken? token, string? error) = await CallProviderAsync(refresh, refreshToken);

        if (token is not null)
        {
            turn.Finish(new RefreshOutcome.Refreshed());
            LogRefreshed(refresh);
            return (await WriteAsync(refresh, turn, record, refreshToken, token, writeFrom), new RefreshOutcome.Refreshed());
        }

        if (error != OidcConstants.TokenErrors.InvalidGrant)
        {
            LogRefreshFailed(refresh, error!);
            return Finished(turn, new AccessTokenResult.ProviderUnavailable(), new RefreshOutcome.Unavailable());
        }

        // Refused. Another writer may have refreshed the set in between, so it rotated the refresh token
        // this call presented: then the record holds its successor, and its access token is good.
        SessionRecord? reread = await turn.RereadAsync();
        if (reread is not null
            && Deserialize(reread).Properties is var current
            && refresh.Set.Token(current, TokenSet.RefreshTokenName) is { } newer
            && !string.Equals(newer, refreshToken, StringComparison.Ordinal)
            && refresh.Set.Token(current, TokenSet.AccessTokenName) is { } newerAccess)
        {
            return Finished(turn, new AccessTokenResult.Token(newerAccess), new RefreshOutcome.Refreshed());
        }

        if (refresh.Set.Fragment is { } fragment)
        {
            return Finished(turn, await EndFragmentSetAsync(refresh, fragment, turn, refreshToken), new RefreshOutcome.Ended());
        }

        // The shell's refusal ends the record, unconditionally (the W3 row).
        await EndAsync(context, refresh.App, turn);
        Ended(logger, refresh.App.AppId, SessionRef.Of(refresh.Key));
        return Finished(turn, new AccessTokenResult.SessionEnded(), new RefreshOutcome.Ended());
    }

    /// <summary>
    /// A fragment's <c>invalid_grant</c> ends that set alone (#995 -> "Expiry"): a bounded versioned
    /// replace that removes the set only while it still holds the refresh token the refresh presented,
    /// so a set another writer has since replaced is left as it is. The record and every other set stay,
    /// no cookie is touched, and the call answers 401 <c>fragment_login_required</c>, also when the
    /// removal ran out of attempts. A record gone on the re-read, which the bounded write reads as missing, authenticates nobody.
    /// </summary>
    private async Task<AccessTokenResult> EndFragmentSetAsync(Refresh refresh, FragmentDefinition fragment, AppSessions.Turn turn, string presented)
    {
        MergeResult result;
        try
        {
            result = await turn.MergeAsync(
                current => Changed(current, p =>
                {
                    if (!string.Equals(refresh.Set.Token(p, TokenSet.RefreshTokenName), presented, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    FragmentSets.Remove(p, fragment.FragmentId);
                    return true;
                }),
                WriteAttempts, options.StoreDeadline, clock);
        }
        catch (MergeFailedException e)
        {
            FragmentEnded(logger, refresh.App.AppId, fragment.FragmentId, SessionRef.Of(refresh.Key), "unconfirmed", e.Attempts);
            throw e.Failure;
        }

        FragmentEnded(logger, refresh.App.AppId, fragment.FragmentId, SessionRef.Of(refresh.Key), MergeOutcomeName(result.Outcome), result.Attempts);
        return result.Outcome == MergeOutcome.Missing ? new AccessTokenResult.Unauthenticated() : new AccessTokenResult.FragmentLoginRequired();
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
    private async Task ReleaseAsync(IRefreshLeases table, Refresh refresh, string leaseKey, RefreshLease lease, string outcome)
    {
        try
        {
            await table.ReleaseAsync(leaseKey, lease, CancellationToken.None);
        }
        catch (Exception e)
        {
            LogReleaseFailed(refresh, outcome, e.GetType().Name);
        }
    }

    private static string OutcomeName(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.Refreshed => "refreshed",
        RefreshOutcome.Unavailable => "unavailable",
        RefreshOutcome.Ended => "ended",
        RefreshOutcome.AtStoreBound => "store_bound",
        _ => "unknown",
    };

    private static string MergeOutcomeName(MergeOutcome outcome) => outcome switch
    {
        MergeOutcome.Written => "written",
        MergeOutcome.Unchanged => "unchanged",
        MergeOutcome.Missing => "missing",
        MergeOutcome.Exhausted => "exhausted",
        _ => throw new InvalidOperationException($"No event outcome for {outcome}."),
    };

    /// <summary>The set's access token when it is not due, else null (plan row 3.8's one-minute margin).</summary>
    private string? FreshAccess(SessionRecord record, TokenSet set)
    {
        AuthenticationProperties properties = Deserialize(record).Properties;
        return set.Token(properties, TokenSet.AccessTokenName) is { } access
            && set.ExpiresAt(properties) > clock.GetUtcNow() + RefreshBeforeExpiry
                ? access
                : null;
    }

    /// <summary>
    /// The library's refresh call, with the set's scheme and the app's sign-in scheme and the issuer's
    /// credential style, under a deadline tied to no request. A fragment's scheme carries the fragment's
    /// client and the shell's issuer (OidcOptionsSetup), so its refresh authenticates as the fragment's
    /// client. Returns the new tokens, or the failure as a fixed category.
    /// </summary>
    private async Task<(UserToken? Token, string? Error)> CallProviderAsync(Refresh refresh, string refreshToken)
    {
        UserTokenRequestParameters parameters = new()
        {
            ChallengeScheme = Scheme.Parse(refresh.Set.ChallengeScheme(refresh.App)),
            SignInScheme = Scheme.Parse(SchemeNames.Cookie(refresh.App.AppId)),
        };

        using CancellationTokenSource deadline = new(options.ProviderDeadline);
        try
        {
            TokenResult<UserToken> result = await endpoints.For(styles.Of(refresh.App)).RefreshAccessTokenAsync(
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
    /// Writes the refresh's answer into its own set of the record, one version on, under the W3 rules
    /// (#995 -> "Writes after a lost versioned replace"):
    /// <list type="bullet">
    /// <item>A lost replace re-reads. A null re-read (removed, expired, rebound) stops the write, which
    /// resurrects nothing, and answers <see cref="AccessTokenResult.Unauthenticated"/>.</item>
    /// <item>The set is re-applied onto the winner only while the winner's set still holds the refresh
    /// token this refresh presented. Otherwise the refresh is superseded and drops its tokens, answering
    /// the winner's token when it is fresh, else <see cref="AccessTokenResult.FragmentLoginRequired"/>
    /// for a fragment and <see cref="AccessTokenResult.ProviderUnavailable"/> for the shell.</item>
    /// <item>At most <see cref="WriteAttempts"/> attempts, each started only if its store calls fit before
    /// the write deadline, <see cref="AccessTokenSourceOptions.WriteDeadline"/> after
    /// <paramref name="writeFrom"/>: one <c>StoreDeadline</c> for the first, the replace the lease budget
    /// reserves, and two for each re-apply, its replace and the re-read before it. Out of attempts, the
    /// call answers its own unpersisted access token, with a Warning.</item>
    /// <item>A store failure is unknown: it leaves as the store's exception, which answers 503
    /// <c>session_store_unavailable</c>.</item>
    /// </list>
    /// One event line carries the write's <c>store_attempts</c> and outcome on every path, the exhausted
    /// and the failed ones included: G9's O2 evidence (Q35).
    /// </summary>
    private async Task<AccessTokenResult> WriteAsync(
        Refresh refresh, AppSessions.Turn turn, SessionRecord record, string presented, UserToken token, long writeFrom)
    {
        string access = token.AccessToken.ToString();
        SessionRecord current = record;
        int attempts = 0;
        try
        {
            while (true)
            {
                TimeSpan needed = attempts == 0 ? options.StoreDeadline : options.StoreDeadline * 2;
                if (attempts >= WriteAttempts || clock.GetElapsedTime(writeFrom) + needed > options.WriteDeadline)
                {
                    LogWrite(refresh, "exhausted", attempts);
                    LogWriteExhausted(refresh, attempts);
                    return new AccessTokenResult.Token(access);
                }

                AuthenticationTicket ticket = Deserialize(current);
                refresh.Set.Apply(ticket.Properties, token);
                attempts++;
                if (await turn.ReplaceAsync(current with { Version = current.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) }))
                {
                    LogWrite(refresh, "written", attempts);
                    return new AccessTokenResult.Token(access);
                }

                if (await turn.RereadAsync() is not { } winner)
                {
                    LogWrite(refresh, "missing", attempts);
                    return new AccessTokenResult.Unauthenticated();
                }

                if (!string.Equals(refresh.Set.Token(Deserialize(winner).Properties, TokenSet.RefreshTokenName), presented, StringComparison.Ordinal))
                {
                    // Another writer replaced the set since this refresh read it: this refresh's tokens are
                    // dropped, and the winner's set is the answer.
                    LogWrite(refresh, "superseded", attempts);
                    return Superseded(refresh, winner);
                }

                current = winner;
            }
        }
        catch (SessionStoreUnavailableException)
        {
            LogWrite(refresh, "unconfirmed", attempts);
            throw;
        }
    }

    /// <summary>The answer of a superseded refresh: the winner's set's token when it is fresh.</summary>
    private AccessTokenResult Superseded(Refresh refresh, SessionRecord winner)
    {
        if (FreshAccess(winner, refresh.Set) is { } fresh)
        {
            return new AccessTokenResult.Token(fresh);
        }

        if (refresh.Set.Fragment is not null)
        {
            return new AccessTokenResult.FragmentLoginRequired();
        }

        // The winner is a live session with no token to hand back. ProviderUnavailable is the one outcome
        // that keeps a session without a token (change request 4.2), and the next call, finding no fresh
        // access token, tries a refresh with the winner's refresh token.
        LostToRecordWithoutToken(logger, refresh.App.AppId, SessionRef.Of(refresh.Key));
        return new AccessTokenResult.ProviderUnavailable();
    }

    /// <summary>The record with its ticket's properties changed by <paramref name="change"/>, or null when it changed nothing.</summary>
    private static SessionRecord? Changed(SessionRecord record, Func<AuthenticationProperties, bool> change)
    {
        AuthenticationTicket ticket = Deserialize(record);
        return change(ticket.Properties) ? record with { Ticket = TicketSerializer.Default.Serialize(ticket) } : null;
    }

    /// <summary>
    /// Removes the session, then clears the cookie. The record goes under the gate and with no
    /// cancellation, so a waiter can trust what it reads; the sign-out then expires the browser's cookie,
    /// unless the browser has already gone. The shell's set alone ends a session this way.
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

    // Each line names the app, the session's reference and, for a fragment's set, the fragment: never
    // the session key, the lease key that carries it, or a token (contracts/telemetry.md, N-16).
    private void LogRefreshed(Refresh r)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentRefreshed(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key));
        }
        else
        {
            Refreshed(logger, r.App.AppId, SessionRef.Of(r.Key));
        }
    }

    private void LogRefreshFailed(Refresh r, string error)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentRefreshFailed(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key), error);
        }
        else
        {
            RefreshFailed(logger, r.App.AppId, SessionRef.Of(r.Key), error);
        }
    }

    private void LogWrite(Refresh r, string outcome, int attempts)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentRefreshWrite(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key), outcome, attempts);
        }
        else
        {
            RefreshWrite(logger, r.App.AppId, SessionRef.Of(r.Key), outcome, attempts);
        }
    }

    private void LogWriteExhausted(Refresh r, int attempts)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentWriteExhausted(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key), attempts);
        }
        else
        {
            WriteExhausted(logger, r.App.AppId, SessionRef.Of(r.Key), attempts);
        }
    }

    private void LogTooLittleLease(Refresh r)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentTooLittleLease(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key));
        }
        else
        {
            TooLittleLease(logger, r.App.AppId, SessionRef.Of(r.Key));
        }
    }

    private void LogReleaseFailed(Refresh r, string outcome, string errorType)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentReleaseFailed(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key), outcome, errorType);
        }
        else
        {
            ReleaseFailed(logger, r.App.AppId, SessionRef.Of(r.Key), outcome, errorType);
        }
    }

    private void LogNoLease(Refresh r)
    {
        if (r.Set.Fragment is { } f)
        {
            FragmentNoLease(logger, r.App.AppId, f.FragmentId, SessionRef.Of(r.Key));
        }
        else
        {
            NoLease(logger, r.App.AppId, SessionRef.Of(r.Key));
        }
    }

    /// <summary>One call's refresh: the app, the session's key and the token set it is for.</summary>
    private sealed record Refresh(AppDefinition App, string Key, TokenSet Set)
    {
        /// <summary>A waiter's answer after the refresh it queued behind ended its set (T-D6).</summary>
        public AccessTokenResult EndedAnswer => Set.Fragment is null
            ? new AccessTokenResult.SessionEnded()
            : new AccessTokenResult.FragmentLoginRequired();

        // The key never reaches a string a log or an exception could carry.
        public override string ToString() => nameof(Refresh);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Refreshed the access token for app {AppId}, fragment {FragmentId}, session {SessionRef}.")]
    private static partial void FragmentRefreshed(ILogger logger, string appId, string fragmentId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The identity provider did not refresh the access token for app {AppId}, fragment {FragmentId}, session {SessionRef} ({Error}). The session is kept.")]
    private static partial void FragmentRefreshFailed(ILogger logger, string appId, string fragmentId, string sessionRef, string error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Refresh write for app {AppId}, session {SessionRef}: ended {Outcome} after {StoreAttempts} versioned replaces.")]
    private static partial void RefreshWrite(ILogger logger, string appId, string sessionRef, string outcome, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Refresh write for app {AppId}, fragment {FragmentId}, session {SessionRef}: ended {Outcome} after {StoreAttempts} versioned replaces.")]
    private static partial void FragmentRefreshWrite(ILogger logger, string appId, string fragmentId, string sessionRef, string outcome, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The refresh for app {AppId}, session {SessionRef} could not write its tokens within its write deadline after {StoreAttempts} versioned replaces: the call answers its own token, unpersisted, and the record keeps the one it held.")]
    private static partial void WriteExhausted(ILogger logger, string appId, string sessionRef, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The refresh for app {AppId}, fragment {FragmentId}, session {SessionRef} could not write its tokens within its write deadline after {StoreAttempts} versioned replaces: the call answers its own token, unpersisted, and the record keeps the one it held.")]
    private static partial void FragmentWriteExhausted(ILogger logger, string appId, string fragmentId, string sessionRef, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The identity provider refused the refresh token for app {AppId}, fragment {FragmentId}, session {SessionRef}, so that fragment's token set has ended; the removal ended {Outcome} after {StoreAttempts} versioned replaces. The session is kept.")]
    private static partial void FragmentEnded(ILogger logger, string appId, string fragmentId, string sessionRef, string outcome, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Too little of the refresh lease for app {AppId}, fragment {FragmentId}, session {SessionRef} remained to call the identity provider. It was given back, and the call waits for another holder's refresh.")]
    private static partial void FragmentTooLittleLease(ILogger logger, string appId, string fragmentId, string sessionRef);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The refresh lease for app {AppId}, fragment {FragmentId}, session {SessionRef} was not given back after the refresh ended ({Outcome}): {ErrorType}. It lapses on its own.")]
    private static partial void FragmentReleaseFailed(ILogger logger, string appId, string fragmentId, string sessionRef, string outcome, string errorType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No refresh lease for app {AppId}, fragment {FragmentId}, session {SessionRef} could be held long enough to refresh, within two times to live. The session is kept.")]
    private static partial void FragmentNoLease(ILogger logger, string appId, string fragmentId, string sessionRef);
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
    /// A refresh's write deadline, measured from its acquire's send (or, with no lease table, from its
    /// gated read): <c>LeaseTimeToLive - 1 s</c>, 20 s today (#995). No write attempt starts unless its
    /// store calls fit before it, so a holder whose lease has lapsed never writes over its successor
    /// (Trace 5).
    /// </summary>
    public TimeSpan WriteDeadline => LeaseTimeToLive - Allowance;

    /// <summary>
    /// How long logout waits, in all, to enter the session's gates: the refresh write deadline, the longest
    /// a holder keeps a gate (#995 -> "Logout"). A gate not entered within it is skipped as
    /// <c>gate_timeout</c>.
    /// </summary>
    public TimeSpan LogoutGateBudget => WriteDeadline;

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
