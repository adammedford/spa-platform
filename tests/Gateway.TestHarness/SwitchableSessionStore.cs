using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.TestHarness;

/// <summary>
/// The cell's one session store in a harness with more than one gateway host: an
/// <see cref="InMemorySessionStore"/> every host reads, as every task of a cell reads the one table (plan
/// row P2-1), with a switch that makes every call fail, as an unreachable store does (#332, F17).
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

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => Gate(() => inner.CreateAsync(record, ct));

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) => Gate(() => inner.GetAsync(key, ct));

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
        Gate(() => inner.ReplaceAsync(key, record, expectedVersion, ct));

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
