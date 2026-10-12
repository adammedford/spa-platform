using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="ISessionStore"/> in memory, for every workstream that needs a logged-in session
/// without an identity stack. Proved by <see cref="SessionStoreContract"/>.
///
/// No capacity bound and no expiry sweep: those belong to task 3.4's store, and a double that evicted
/// under load would make another workstream's test flake.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionRecord> sessions = new(StringComparer.Ordinal);

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string key = Guid.NewGuid().ToString("n");
        sessions[key] = record;
        return Task.FromResult(key);
    }

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(sessions.TryGetValue(key, out SessionRecord? record) ? record : null);
    }

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!sessions.TryGetValue(key, out SessionRecord? current) || current.Version != expectedVersion)
        {
            return Task.FromResult(false);
        }

        // Compare-and-swap on the record we matched, so two writers at the same expected version
        // cannot both succeed.
        return Task.FromResult(sessions.TryUpdate(key, record, current));
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        sessions.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveAppAsync(string appId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (KeyValuePair<string, SessionRecord> session in sessions)
        {
            if (string.Equals(session.Value.AppId, appId, StringComparison.Ordinal))
            {
                sessions.TryRemove(session);
            }
        }

        return Task.CompletedTask;
    }
}
