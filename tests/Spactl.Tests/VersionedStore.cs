using System.Collections.Concurrent;
using System.Globalization;
using Platform.Abstractions;
using Platform.Testing;
using Spactl;

namespace Spactl.Tests;

/// <summary>
/// The Platform.Testing <see cref="InMemoryObjectStore"/>, which enforces the bucket's conditional-write
/// rules, with the version history a versioned bucket keeps (contracts/s3-layout.md: the registry
/// object keeps every version). Every successful write is recorded as a new version, newest first, as
/// S3's ListObjectVersions returns them; a refused write records nothing.
///
/// The S3 history meets a real bucket in task 6.9; this stands in for it the way the in-memory store
/// stands in for <see cref="Platform.Registry.S3ObjectStore"/>.
/// </summary>
internal sealed class VersionedStore : IObjectStore, IRegistryHistory
{
    private readonly InMemoryObjectStore inner = new();
    private readonly ConcurrentDictionary<string, List<(string VersionId, byte[] Body)>> versions = new(StringComparer.Ordinal);
    private int nextVersion;

    /// <summary>Every key written, in write order: what a release publish sent.</summary>
    public List<string> Written { get; } = [];

    /// <summary>Every key read, in read order.</summary>
    public List<string> Read { get; } = [];

    public async Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        lock (Read)
        {
            Read.Add(key);
        }

        return await inner.GetAsync(key, ifNoneMatch, ct);
    }

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        using MemoryStream copy = new();
        await body.CopyToAsync(copy, ct);
        byte[] bytes = copy.ToArray();

        if (!await inner.PutAsync(key, new MemoryStream(bytes), ifMatch, ct))
        {
            return false;
        }

        lock (Written)
        {
            Written.Add(key);
            versions.GetOrAdd(key, _ => []).Insert(0, (Interlocked.Increment(ref nextVersion).ToString("x8", CultureInfo.InvariantCulture), bytes));
        }

        return true;
    }

    public Task<IReadOnlyList<string>> ListVersionsAsync(string key, CancellationToken ct)
    {
        lock (Written)
        {
            IReadOnlyList<string> ids = versions.TryGetValue(key, out var list) ? [.. list.Select(v => v.VersionId)] : [];
            return Task.FromResult(ids);
        }
    }

    public Task<Stream> OpenVersionAsync(string key, string versionId, CancellationToken ct)
    {
        lock (Written)
        {
            byte[] body = versions[key].Single(v => v.VersionId == versionId).Body;
            return Task.FromResult<Stream>(new MemoryStream(body, writable: false));
        }
    }

    /// <summary>The object's current bytes as text, or null when the key is absent.</summary>
    public async Task<string?> TextAsync(string key)
    {
        StoredObject? stored = await inner.GetAsync(key, null, CancellationToken.None);
        if (stored?.Body is null)
        {
            return null;
        }

        using StreamReader reader = new(stored.Body);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Writes a key directly, as an earlier run or another operator would have.</summary>
    public Task<bool> SeedAsync(string key, string text) =>
        PutAsync(key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), null, CancellationToken.None);

    public int VersionCount(string key) => versions.TryGetValue(key, out var list) ? list.Count : 0;
}
