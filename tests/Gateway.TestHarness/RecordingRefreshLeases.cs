using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.TestHarness;

/// <summary>
/// The cell's one refresh-lease table in a harness with more than one gateway host: an
/// <see cref="InMemoryRefreshLeases"/> every host asks (plan row P2-3), which counts what it answered, so a
/// composed row can tell when a host has become a loser and when the lease it waits on lapses (#346,
/// #332 F3). It changes no answer.
///
/// The leases it hands out are kept for their <see cref="RefreshLease.ExpiresAt"/>. Their tokens fence a
/// release and authenticate nobody (change request P2-3, Decision item 5), and nothing here prints one.
/// </summary>
public sealed class RecordingRefreshLeases(TimeProvider clock) : IRefreshLeases
{
    private readonly InMemoryRefreshLeases inner = new(clock);
    private readonly object gate = new();
    private readonly List<RefreshLease> acquired = [];
    private int refused;

    /// <summary>Every lease taken, in the order taken.</summary>
    public IReadOnlyList<RefreshLease> Acquired
    {
        get
        {
            lock (gate)
            {
                return [.. acquired];
            }
        }
    }

    /// <summary>How many acquisitions were answered null: a caller found the lease held and became a loser.</summary>
    public int Refused => Volatile.Read(ref refused);

    public async Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
    {
        RefreshLease? lease = await inner.TryAcquireAsync(sessionKey, ttl, ct);
        if (lease is null)
        {
            Interlocked.Increment(ref refused);
        }
        else
        {
            lock (gate)
            {
                acquired.Add(lease);
            }
        }

        return lease;
    }

    public Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct) => inner.ReleaseAsync(sessionKey, lease, ct);
}
