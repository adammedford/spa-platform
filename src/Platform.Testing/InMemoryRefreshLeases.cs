using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IRefreshLeases"/> in memory, for the two-host proofs that share one instance between
/// two gateways (change request P2-3 Decision item 7). Proved by <see cref="RefreshLeasesContract"/>.
///
/// Every expiry reads <see cref="TimeProvider"/>, so a test moves a <see cref="TestClock"/> instead of
/// waiting. A lease is held up to and including its <see cref="RefreshLease.ExpiresAt"/> and may be
/// taken over after it, as the DynamoDB lease's <c>leaseUntil &lt; :now</c> reads.
/// </summary>
public sealed class InMemoryRefreshLeases(TimeProvider clock) : IRefreshLeases
{
    private readonly ConcurrentDictionary<string, RefreshLease> leases = new(StringComparer.Ordinal);

    public Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        DateTimeOffset now = clock.GetUtcNow();
        RefreshLease fresh = new(Guid.NewGuid().ToString("n"), now + ttl);

        // One acquire answers as one conditional write would: null only when it saw a lease that had
        // not expired. A key released between the add and the read, or a lapsed lease another caller
        // changed first, is looked at again rather than answered null, because a caller given null
        // waits as a loser (ADR-0024 decision 4) and a free key must not send it there. Each pass
        // either answers or saw another writer's change, so the loop ends.
        while (true)
        {
            if (leases.TryAdd(sessionKey, fresh))
            {
                return Task.FromResult<RefreshLease?>(fresh);
            }

            if (!leases.TryGetValue(sessionKey, out RefreshLease? current))
            {
                continue;
            }

            if (current.ExpiresAt >= now)
            {
                return Task.FromResult<RefreshLease?>(null);
            }

            // Compare-and-swap on the expired lease just read, so two callers taking over one lapsed
            // lease cannot both succeed.
            if (leases.TryUpdate(sessionKey, fresh, current))
            {
                return Task.FromResult<RefreshLease?>(fresh);
            }
        }
    }

    public Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        // Removes the entry only while it is this very lease: a successor's lease, or nothing, is
        // left alone (ADR-0024 decision 5).
        leases.TryRemove(KeyValuePair.Create(sessionKey, lease));
        return Task.CompletedTask;
    }
}
