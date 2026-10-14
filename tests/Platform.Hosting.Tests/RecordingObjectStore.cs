using System.Collections.Concurrent;
using System.Text;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// <see cref="InMemoryObjectStore"/> with every key read recorded, so a test can say what the asset
/// endpoint asked the bucket for and not only what it answered (N-13: no object outside the app's
/// release prefix is read). Seeding goes through the inner store's own write-once path.
/// </summary>
internal sealed class RecordingObjectStore : IObjectStore
{
    private readonly InMemoryObjectStore inner = new();
    private readonly ConcurrentQueue<(string Key, string? IfNoneMatch)> reads = new();

    public IReadOnlyList<string> KeysRead => [.. reads.Select(read => read.Key)];

    public IReadOnlyList<string?> IfNoneMatchSent => [.. reads.Select(read => read.IfNoneMatch)];

    public async Task SeedAsync(string key, string body)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        if (!await inner.PutAsync(key, stream, ifMatch: null, CancellationToken.None))
        {
            throw new InvalidOperationException($"{key} was seeded twice.");
        }
    }

    public async Task<string> ETagOfAsync(string key) =>
        (await inner.GetAsync(key, ifNoneMatch: null, CancellationToken.None))?.ETag
        ?? throw new InvalidOperationException($"{key} was never seeded.");

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        reads.Enqueue((key, ifNoneMatch));
        return inner.GetAsync(key, ifNoneMatch, ct);
    }

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        throw new InvalidOperationException("The gateway cannot write (contracts/s3-layout.md, P-03).");
}
