using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// A session store whose calls fail on cue, as the DynamoDB store does when a call fails or runs out
/// <c>StoreDeadline</c> (F17): it throws the store's "did not complete". Once <see cref="FailRead"/> is
/// set, the read with that number, counting from one after it was set, throws; while
/// <see cref="FailCreate"/> or <see cref="FailRemove"/> is set, every create or remove throws. Every other
/// call passes through to <paramref name="inner"/>.
/// </summary>
internal sealed class ScriptedReads(ISessionStore inner) : ISessionStore
{
    private int? failRead;
    private int reads;

    public int? FailRead
    {
        get => failRead;
        set
        {
            failRead = value;
            reads = 0;
        }
    }

    public bool FailCreate { get; set; }

    public bool FailRemove { get; set; }

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) =>
        FailCreate ? throw Unavailable("create (PutItem)") : inner.CreateAsync(record, ct);

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) =>
        failRead is { } n && Interlocked.Increment(ref reads) == n
            ? throw Unavailable("read (GetItem)")
            : inner.GetAsync(key, ct);

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
        inner.ReplaceAsync(key, record, expectedVersion, ct);

    public Task RemoveAsync(string key, CancellationToken ct) =>
        FailRemove ? throw Unavailable("remove (DeleteItem)") : inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);

    private static SessionStoreUnavailableException Unavailable(string operation) =>
        new(operation, DynamoDbSessionTable.DefaultStoreDeadline, null);
}
