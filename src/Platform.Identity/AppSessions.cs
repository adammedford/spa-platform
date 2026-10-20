using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The one module through which the gateway reads, changes and ends a session under the session gate,
/// above the frozen <see cref="ISessionStore"/> seam (#141). It holds the app-and-client ownership check
/// once (task 3.3, task 3.6), the versioned replace and the end that removes whatever its action did
/// (ADR-0017 decision 4). <see cref="AccessTokenSource"/> and <see cref="SessionEnd"/> keep the decisions
/// and the outcome lines; this module never logs.
///
/// One refresh at a time per session (ADR-0017 decision 1, ADR-0005's reserve design, T-D1 to T-D10).
/// Cognito granted a refresh token replayed 2.0 seconds inside its grace window and then refused the
/// first grant's successor with <c>invalid_grant</c> (C-2.17), so two refreshes racing on one session
/// can leave it holding a token the provider has already superseded. The token library's own
/// single-flight does not cover a request that read the session before another request's refresh
/// finished (T-B6). A gate per session key does: whoever holds it reads the session from the store,
/// decides, refreshes and writes, and the next holder reads what was written (T-D2, T-D3).
///
/// A gate also carries the outcome of the last refresh it ran, so the calls that queued behind a
/// refresh share its outage rather than each asking the provider again (T-D6). The lease carries and
/// compares that outcome and never interprets it: which outcome a refresh had is the token source's
/// decision. A gate exists only while someone holds or waits for it, so the table is bounded by the
/// calls in flight, not by the sessions that ever refreshed (<see cref="ActiveGates"/>).
///
/// In process only. Two gateway tasks share no gate; the re-read before a refusal ends a session
/// (T-B8) and the session store's versioned replace are what cover that case.
/// </summary>
internal sealed class AppSessions(ISessionStore store)
{
    private readonly ISessionStore store = store;
    private readonly Dictionary<string, Gate> gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for the session's gate, then reads the record under it. Never cancelled: a waiter that gives
    /// up changes nothing, and the request's cancellation token must not reach the refresh (T-B15, T-B16).
    /// </summary>
    /// <param name="app">The app the caller acts for; a record that is not its and its client's is not owned.</param>
    public async Task<Lease> EnterAsync(AppDefinition app, string sessionKey)
    {
        Gate gate;
        long finished;
        lock (gates)
        {
            if (!gates.TryGetValue(sessionKey, out gate!))
            {
                gate = new Gate();
                gates[sessionKey] = gate;
            }

            gate.Users++;
            finished = gate.Finished;
        }

        await gate.Semaphore.WaitAsync(CancellationToken.None);
        SessionRecord? owned;
        try
        {
            // Read once the gate is held, so the holder decides on what the previous holder wrote.
            owned = OwnedBy(app, await store.GetAsync(sessionKey, CancellationToken.None));
        }
        catch
        {
            Leave(sessionKey, gate);
            throw;
        }

        return new Lease(this, sessionKey, gate, finished, owned);
    }

    /// <summary>
    /// The record under the key, ungated, when it is this app's and this client's; null for an absent
    /// record and a foreign one alike, so a caller cannot tell them apart and must not try (N-02, N-22).
    /// A read changes nothing, so it takes the caller's token.
    /// </summary>
    public async Task<SessionRecord?> ReadAsync(AppDefinition app, string sessionKey, CancellationToken cancellationToken) =>
        OwnedBy(app, await store.GetAsync(sessionKey, cancellationToken));

    /// <summary>The number of gates held or waited for: the bound on the table, for a test.</summary>
    internal int ActiveGates
    {
        get
        {
            lock (gates)
            {
                return gates.Count;
            }
        }
    }

    /// <summary>
    /// The ownership check, written once: a session is bound to the app and to the OAuth client it was
    /// established with, so rotating an app onto a new client orphans its sessions (task 3.3, task 3.6).
    /// </summary>
    private static SessionRecord? OwnedBy(AppDefinition app, SessionRecord? record) =>
        record is not null
        && string.Equals(record.AppId, app.AppId, StringComparison.Ordinal)
        && string.Equals(record.ClientId, app.OAuth.ClientId, StringComparison.Ordinal)
            ? record
            : null;

    private void Leave(string sessionKey, Gate gate)
    {
        gate.Semaphore.Release();
        lock (gates)
        {
            if (--gate.Users == 0)
            {
                gates.Remove(sessionKey);
            }
        }
    }

    internal sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders and waiters. Guarded by the table's lock.</summary>
        public int Users { get; set; }

        /// <summary>How many refreshes this gate has seen to an end. Written only by the holder.</summary>
        public long Finished { get; set; }

        /// <summary>How the last of them ended. Written only by the holder.</summary>
        public RefreshOutcome? Last { get; set; }
    }

    /// <summary>
    /// A held gate and the record read under it. Disposing it lets the next waiter in. Its operations run
    /// with <see cref="CancellationToken.None"/>: a disconnecting browser strands no rotated refresh token
    /// and stops no removal (T-B15, T-B16). A lease over an absent or foreign record owns nothing, and
    /// nothing under it replaces or removes.
    /// </summary>
    internal sealed class Lease(AppSessions owner, string sessionKey, Gate gate, long finishedOnArrival, SessionRecord? owned)
        : IDisposable
    {
        private bool released;

        /// <summary>
        /// The record read under the gate when it is this app's and this client's, else null. After a won
        /// <see cref="ReplaceAsync"/> it is the record written, and after <see cref="EndAsync"/> it is null.
        /// </summary>
        public SessionRecord? Owned { get; private set; } = owned;

        /// <summary>
        /// How the refresh that ran while this caller waited ended, or null when none did: a caller that
        /// queued behind a refresh takes its outcome rather than repeating it (T-D6).
        /// </summary>
        public RefreshOutcome? WhileWaiting => gate.Finished != finishedOnArrival ? gate.Last : null;

        /// <summary>Records how the refresh this holder ran ended, for the callers waiting behind it.</summary>
        public void Finish(RefreshOutcome outcome)
        {
            gate.Last = outcome;
            gate.Finished++;
        }

        /// <summary>
        /// Replaces the owned record with <paramref name="next"/>, only if the store still holds it at
        /// <see cref="Owned"/>'s version (T-D2). True when this write won; false when another writer got
        /// there first, which leaves the winner in place and changes nothing. What a loss means is the
        /// caller's to decide.
        /// </summary>
        public async Task<bool> ReplaceAsync(SessionRecord next)
        {
            if (Owned is not { } current
                || !await owner.store.ReplaceAsync(sessionKey, next, current.Version, CancellationToken.None))
            {
                return false;
            }

            Owned = next;
            return true;
        }

        /// <summary>
        /// Ends the owned session: runs <paramref name="beforeRemoval"/> on the owned record, then removes
        /// it in a <c>finally</c>, so the action failing never stops the removal, and what the action
        /// throws reaches the caller after the removal has run ("revoke, then remove whatever the provider
        /// said", ADR-0017 decision 4). Removed under the gate, so a caller queued behind this one finds
        /// no session.
        /// </summary>
        public async Task EndAsync(Func<SessionRecord, Task>? beforeRemoval = null)
        {
            if (Owned is not { } current)
            {
                return;
            }

            try
            {
                if (beforeRemoval is not null)
                {
                    await beforeRemoval(current);
                }
            }
            finally
            {
                Owned = null;
                await owner.store.RemoveAsync(sessionKey, CancellationToken.None);
            }
        }

        public void Dispose()
        {
            if (!released)
            {
                released = true;
                owner.Leave(sessionKey, gate);
            }
        }
    }
}

/// <summary>How a refresh ended, as the callers queued behind it need to know.</summary>
internal abstract record RefreshOutcome
{
    /// <summary>New tokens are in the session record; a waiter reads them from there.</summary>
    public sealed record Refreshed : RefreshOutcome;

    /// <summary>The provider refused the refresh token and the session was removed.</summary>
    public sealed record Ended : RefreshOutcome;

    /// <summary>
    /// The provider failed other than with <c>invalid_grant</c>; the session stands, and a waiter fails
    /// with <see cref="Platform.Abstractions.AccessTokenResult.ProviderUnavailable"/> too (T-D6).
    /// </summary>
    public sealed record Unavailable : RefreshOutcome;
}
