using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.TestHarness;

/// <summary>
/// The cell's one session store in a harness with more than one gateway host: an
/// <see cref="InMemorySessionStore"/> every host reads, as every task of a cell reads the one table (plan
/// row P2-1), with a switch that makes every call fail, as an unreachable store does (#332, F17), and one
/// that freezes the next versioned replace, as a task killed between the provider's rotation and its
/// write leaves it (#332 F3, #346).
///
/// The failure is an <see cref="IOException"/>, not a type of the store's own: the composed row is about
/// what the gateway does when a call to its store throws, whichever store it is.
/// </summary>
public sealed class SwitchableSessionStore : ISessionStore
{
    private readonly InMemorySessionStore inner = new();
    private volatile bool failing;

    /// <summary>While set, every call throws before it reaches the records.</summary>
    public bool Failing
    {
        get => failing;
        set => failing = value;
    }

    /// <summary>
    /// While set, <see cref="RemoveAppAsync"/> alone throws: the options invalidator's purge of a rotated
    /// app fails and every other call succeeds, which leaves the old sessions in the store for a host that
    /// has not polled yet (#332, F7).
    /// </summary>
    public bool FailingPurge
    {
        get => failingPurge;
        set => failingPurge = value;
    }

    /// <summary>How many calls were refused while <see cref="Failing"/> or <see cref="FailingPurge"/> was set.</summary>
    public int Refused => Volatile.Read(ref refused);

    private volatile bool failingPurge;
    private int refused;

    private readonly object gate = new();
    private readonly List<string> created = [];
    private bool stallArmed;
    private readonly TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The keys of the sessions created, in the order created. A key is a bearer credential (N-16), so a
    /// test reads a record with it and never prints it.
    /// </summary>
    public IReadOnlyList<string> Created
    {
        get
        {
            lock (gate)
            {
                return [.. created];
            }
        }
    }

    /// <summary>
    /// Freezes the next <see cref="ReplaceAsync"/>, whichever host sends it, until <see cref="Resume"/>: the
    /// write never lands and the caller never returns, so the refresh lease it holds is never given back.
    /// That is a task killed after the provider rotated and before its write (#332 F3), as workstream 3's
    /// two-gateway row places it (<c>TwoGatewayRefreshTests</c>): a late provider answer alone either
    /// arrives, and the holder writes, or meets the deadline, and the holder gives the lease back (T-D9).
    /// One replace only: the successor's write after it goes through. Armed once per store.
    /// </summary>
    public void StallNextReplace()
    {
        lock (gate)
        {
            if (stallArmed || stalled.Task.IsCompleted)
            {
                throw new InvalidOperationException("the harness's session store stalls one replace per run.");
            }

            stallArmed = true;
        }
    }

    /// <summary>Completes when the replace <see cref="StallNextReplace"/> armed has arrived and is frozen.</summary>
    public Task Stalled => stalled.Task;

    /// <summary>Lets the frozen replace go on to the records, where it meets whatever was written since. Idempotent.</summary>
    public void Resume() => resumed.TrySetResult();

    public async Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        string key = await Gate(() => inner.CreateAsync(record, ct));
        lock (gate)
        {
            created.Add(key);
        }

        return key;
    }

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) => Gate(() => inner.GetAsync(key, ct));

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        bool stall;
        lock (gate)
        {
            stall = stallArmed;
            if (stall)
            {
                stallArmed = false;
                stalled.TrySetResult();
            }
        }

        return stall ? AfterResumeAsync() : Gate(() => inner.ReplaceAsync(key, record, expectedVersion, ct));

        async Task<bool> AfterResumeAsync()
        {
            await resumed.Task;
            return await Gate(() => inner.ReplaceAsync(key, record, expectedVersion, ct));
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct) => Gate(async () =>
    {
        await inner.RemoveAsync(key, ct);
        return true;
    });

    public Task RemoveAppAsync(string appId, CancellationToken ct) => Gate(async () =>
    {
        await inner.RemoveAppAsync(appId, ct);
        return true;
    }, purge: true);

    private Task<T> Gate<T>(Func<Task<T>> call, bool purge = false)
    {
        if (failing || (purge && failingPurge))
        {
            Interlocked.Increment(ref refused);
            return Task.FromException<T>(new IOException("the harness's session store is failing this call."));
        }

        return call();
    }
}
