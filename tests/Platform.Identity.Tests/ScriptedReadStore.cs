using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// A session store whose reads a test can script one at a time: <see cref="OnNextGet"/> runs inside the
/// next <see cref="GetAsync"/>, once, before the read reaches <paramref name="inner"/>. Armed from
/// <see cref="ScriptedLeases.AfterAcquire"/>, it makes the one read under the lease, the token source's
/// re-read, take time or see the host stop, while the gated entry read, the cookie handler's resolve and
/// a loser's polls pass straight through (change request P2-3, second amendment: the acquire and the
/// re-read together). Every other call goes to <paramref name="inner"/>.
/// </summary>
internal sealed class ScriptedReadStore(ISessionStore inner) : ISessionStore
{
    private Func<Task>? onNextGet;

    /// <summary>Runs inside the next read, once; null until armed again.</summary>
    public Func<Task>? OnNextGet
    {
        get => Volatile.Read(ref onNextGet);
        set => Volatile.Write(ref onNextGet, value);
    }

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

    public async Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref onNextGet, null) is { } pending)
        {
            await pending();
        }

        return await inner.GetAsync(key, ct);
    }

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
        inner.ReplaceAsync(key, record, expectedVersion, ct);

    public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);
}
