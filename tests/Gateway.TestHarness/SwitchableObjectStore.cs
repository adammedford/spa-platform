using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Gateway.TestHarness;

/// <summary>
/// The cell's bucket as the gateway's role reads it: the harness's <see cref="GatewayHarness.Store"/>,
/// with a switch that refuses reads of a key the bucket holds, as a role denied that key is refused
/// (#429). That is the second of the two cases <see cref="ObjectReadRefusedException"/> stands for, "an
/// existing key the role may not read" (<see cref="IObjectStore.GetAsync"/>); the first, a missing key
/// under a role without listing, is the store's own refusal mode
/// (<see cref="GatewayHarnessOptions.MissingKeys"/>).
///
/// The refusal carries no inner exception, as <c>Platform.Testing</c>'s in-memory refusal does: there is
/// no SDK exception here, and an invented one would read as a cause.
/// </summary>
public sealed class SwitchableObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly ConcurrentDictionary<string, bool> refused = new(StringComparer.Ordinal);
    private int refusals;

    /// <summary>From now on, every read of <paramref name="key"/> is refused, whether the bucket holds it or not.</summary>
    public void Refuse(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        refused[key] = true;
    }

    /// <summary>Reads of <paramref name="key"/> reach the bucket again.</summary>
    public void Allow(string key) => refused.TryRemove(key, out _);

    /// <summary>How many reads this switch refused, so a test can tell its refusal reached the reader.</summary>
    public int Refusals => Volatile.Read(ref refusals);

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        // Cancellation first, as the in-memory store orders it: a cancelled read is never a refusal.
        ct.ThrowIfCancellationRequested();
        if (refused.ContainsKey(key))
        {
            Interlocked.Increment(ref refusals);
            return Task.FromException<StoredObject?>(new ObjectReadRefusedException(key, innerException: null));
        }

        return inner.GetAsync(key, ifNoneMatch, ct);
    }

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        inner.PutAsync(key, body, ifMatch, ct);
}
