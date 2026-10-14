using Platform.Abstractions;

namespace Spactl.Tests;

/// <summary>
/// A store where another writer wins the race: just before spactl's first write to
/// <paramref name="key"/>, <paramref name="competingText"/> is written there through the same store.
/// spactl's own write then meets the conditional-write rule with a stale ETag.
/// </summary>
internal sealed class RacingStore(VersionedStore inner, string key, string competingText) : IObjectStore
{
    private bool raced;

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) =>
        inner.GetAsync(key, ifNoneMatch, ct);

    public async Task<bool> PutAsync(string putKey, Stream body, string? ifMatch, CancellationToken ct)
    {
        if (!raced && putKey == key)
        {
            raced = true;
            StoredObject? current = await inner.GetAsync(key, null, ct);
            current?.Body?.Dispose();
            await inner.PutAsync(key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(competingText)), current?.ETag, ct);
        }

        return await inner.PutAsync(putKey, body, ifMatch, ct);
    }
}
