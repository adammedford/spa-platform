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

        if (leases.TryAdd(sessionKey, fresh))
        {
            return Task.FromResult<RefreshLease?>(fresh);
        }

        // Compare-and-swap on the expired lease we read, so two callers taking over one lapsed lease
        // cannot both succeed. A key released in between is retried as free by the caller's next try.
        bool takenOver =
            leases.TryGetValue(sessionKey, out RefreshLease? current) &&
            current.ExpiresAt < now &&
            leases.TryUpdate(sessionKey, fresh, current);

        return Task.FromResult(takenOver ? fresh : null);
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
