using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// An <see cref="ISessionStore"/> that passes every call to the Platform.Testing store and remembers
/// the keys it handed out, so a test can count the sessions a login created and read one back through
/// the store's own interface.
/// </summary>
internal sealed class RecordingSessionStore(ISessionStore inner) : ISessionStore
{
    private readonly ConcurrentQueue<string> created = new();

    /// <summary>Every key <see cref="CreateAsync"/> returned, oldest first, whether or not it was since removed.</summary>
    public IReadOnlyList<string> Created => [.. created];

    public async Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        string key = await inner.CreateAsync(record, ct);
        created.Enqueue(key);
        return key;
    }

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
        inner.ReplaceAsync(key, record, expectedVersion, ct);

    public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);

    /// <summary>The record under the one key created so far.</summary>
    public async Task<(string Key, SessionRecord Record)> SingleAsync()
    {
        string key = Assert.Single(Created);
        return (key, Assert.IsType<SessionRecord>(await GetAsync(key, TestContext.Current.CancellationToken)));
    }
}
