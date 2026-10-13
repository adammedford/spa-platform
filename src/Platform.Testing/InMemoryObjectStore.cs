using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IObjectStore"/> in memory, with the conditional-write rules the bucket enforces.
/// Workstreams 2 and 6 build against this; the S3 store meets a real bucket in task 1.7.
///
/// Bodies are copied in and handed out as fresh streams, so no two readers share a position and a
/// caller cannot mutate what the store holds. Proved by <see cref="ObjectStoreContract"/>.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, Entry> objects = new(StringComparer.Ordinal);
    private long etagCounter;

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!objects.TryGetValue(key, out Entry? entry))
        {
            return Task.FromResult<StoredObject?>(null);
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
