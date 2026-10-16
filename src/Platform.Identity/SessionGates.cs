namespace Platform.Identity;

/// <summary>
/// One refresh at a time per session (ADR-0017 decision 1, ADR-0005's reserve design, T-D1 to T-D10).
///
/// Cognito granted a refresh token replayed 2.0 seconds inside its grace window and then refused the
/// first grant's successor with <c>invalid_grant</c> (C-2.17), so two refreshes racing on one session
/// can leave it holding a token the provider has already superseded. The token library's own
/// single-flight does not cover a request that read the session before another request's refresh
/// finished (T-B6). A gate per session key does: whoever holds it reads the session from the store,
/// decides, refreshes and writes, and the next holder reads what was written (T-D2, T-D3).
///
/// A gate also carries the outcome of the last refresh it ran, so the calls that queued behind a
/// refresh share its outage rather than each asking the provider again (T-D6). A gate exists only
/// while someone holds or waits for it, so the table is bounded by the calls in flight, not by the
/// sessions that ever refreshed.
///
/// In process only. Two gateway tasks share no gate; the re-read before a refusal ends a session
/// (T-B8) and the session store's versioned replace are what cover that case.
/// </summary>
internal sealed class SessionGates
{
    private readonly Dictionary<string, Gate> gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for the session's gate. Never cancelled: a waiter that gives up changes nothing, and the
    /// request's cancellation token must not reach the refresh (T-B15, T-B16).
    /// </summary>
    public async Task<Lease> EnterAsync(string sessionKey)
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
        return new Lease(this, sessionKey, gate, finished);
    }

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

    /// <summary>The number of gates held or waited for: the bound, for a test.</summary>
    internal int Count
    {
        get
        {
            lock (gates)
            {
                return gates.Count;
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

    /// <summary>A held gate. Disposing it lets the next waiter in.</summary>
    internal sealed class Lease(SessionGates owner, string sessionKey, Gate gate, long finishedOnArrival) : IDisposable
    {
        private bool released;

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

    /// <summary>The provider failed other than with <c>invalid_grant</c>; the session stands.</summary>
    public sealed record Unavailable(string Error) : RefreshOutcome;
}
