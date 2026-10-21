using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// An <see cref="ISessionStore"/> that passes every call to the Platform.Testing store and remembers
/// the keys it handed out and the removals it was asked for, so a test can count the sessions a login
/// created, read one back through the store's own interface and see what a removal ended.
/// </summary>
internal sealed class RecordingSessionStore(ISessionStore inner) : ISessionStore
{
    private readonly ConcurrentQueue<string> created = new();
    private readonly ConcurrentQueue<(string Key, SessionRecord? Held)> removed = new();
    private readonly ConcurrentQueue<(string Key, bool? GateHeld)> gated = new();

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

    /// <summary>
    /// Every removal that reached the store, oldest first, with the record the store held under the key
    /// as the removal arrived (null when it held none): what a session held when it was ended, so a test
    /// can order a removal against a write made under the session gate.
    /// </summary>
    public IReadOnlyList<(string Key, SessionRecord? Held)> Removed => [.. removed];

    /// <summary>
    /// Asked as each removal arrives, when a test sets it, and its answers kept in
    /// <see cref="GatedRemovals"/>: a test sets it to whether the session module has a gate held, which
    /// tells a removal made under a lease from the cookie handler's ungated one.
    /// </summary>
    public Func<bool>? GateHeld { get; set; }

    /// <summary>
    /// For each removal, oldest first, what <see cref="GateHeld"/> answered as it arrived. Recorded before
    /// the store is read, so two overlapping removals can sit in a different order in <see cref="Removed"/>.
    /// </summary>
    public IReadOnlyList<(string Key, bool? GateHeld)> GatedRemovals => [.. gated];

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        gated.Enqueue((key, GateHeld?.Invoke()));
        removed.Enqueue((key, await inner.GetAsync(key, ct)));
        await inner.RemoveAsync(key, ct);
    }

    public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);

    /// <summary>The record under the one key created so far.</summary>
    public async Task<(string Key, SessionRecord Record)> SingleAsync()
    {
        string key = Assert.Single(Created);
        return (key, Assert.IsType<SessionRecord>(await GetAsync(key, TestContext.Current.CancellationToken)));
    }
}
