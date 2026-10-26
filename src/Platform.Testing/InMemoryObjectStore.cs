using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IObjectStore"/> in memory, with the conditional-write rules the bucket enforces.
/// Workstreams 2 and 6 build against this; the S3 store meets a real bucket in task 1.7.
///
/// Bodies are copied in and handed out as fresh streams, so no two readers share a position and a
/// caller cannot mutate what the store holds. Proved by <see cref="ObjectStoreContract"/>, in both
/// of its modes.
///
/// <para>A key it does not hold reads as null by default. Constructed with
/// <see cref="MissingKeyAnswer.AmbiguousRefusal"/>, it throws <see cref="ObjectReadRefusedException"/>
/// instead, with no inner exception, which is how the deployed bucket answers the gateway's
/// GetObject-only role (change request contracts/changes/issue-381-object-read-refusal.md, item 2). A
/// key it holds reads the same in both modes.</para>
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, Entry> objects = new(StringComparer.Ordinal);
    private readonly MissingKeyAnswer missingKeys;
    private long etagCounter;

    /// <summary>A store that answers a key it does not hold with null.</summary>
    public InMemoryObjectStore()
        : this(MissingKeyAnswer.ConfirmedAbsence)
    {
    }

    /// <summary>A store that answers a key it does not hold as <paramref name="missingKeys"/> says.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="missingKeys"/> is not a defined mode.</exception>
    public InMemoryObjectStore(MissingKeyAnswer missingKeys)
    {
        if (!Enum.IsDefined(missingKeys))
        {
            throw new ArgumentOutOfRangeException(nameof(missingKeys), missingKeys, "Not a missing-key mode.");
        }

        this.missingKeys = missingKeys;
    }

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        // Cancellation first, in both modes: a cancelled read is never reported as a refusal.
        ct.ThrowIfCancellationRequested();

        if (!objects.TryGetValue(key, out Entry? entry))
        {
            return missingKeys == MissingKeyAnswer.AmbiguousRefusal
                ? Task.FromException<StoredObject?>(new ObjectReadRefusedException(key, innerException: null))
                : Task.FromResult<StoredObject?>(null);
        }

        StoredObject result = string.Equals(ifNoneMatch, entry.ETag, StringComparison.Ordinal)
            ? new StoredObject(Body: null, entry.ETag, Length: null, NotModified: true)
            : new StoredObject(new MemoryStream(entry.Body, writable: false), entry.ETag, entry.Body.Length, NotModified: false);

        return Task.FromResult<StoredObject?>(result);
    }

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using MemoryStream buffer = new();
        await body.CopyToAsync(buffer, ct);
        byte[] bytes = buffer.ToArray();

        if (ifMatch is null)
        {
            // Write-once: create, or refuse. ADR-0009 and P-04.
            return objects.TryAdd(key, new Entry(bytes, NextETag()));
        }

        if (!objects.TryGetValue(key, out Entry? current) || !string.Equals(current.ETag, ifMatch, StringComparison.Ordinal))
        {
            return false;
        }

        // Compare-and-swap on the entry we matched, so two writers racing on the same ETag cannot
        // both succeed.
        return objects.TryUpdate(key, new Entry(bytes, NextETag()), current);
    }

    private string NextETag() => "\"" + Interlocked.Increment(ref etagCounter).ToString("x8") + "\"";

    private sealed record Entry(byte[] Body, string ETag);
}
