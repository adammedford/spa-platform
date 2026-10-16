namespace Platform.Abstractions;

/// <summary>
/// One server-side session: the authentication ticket and what it is bound to. Source:
/// docs/plan/02-contracts.md -> Interfaces. Assigned to G1b (ADR-0017); the <c>contracts-v1</c> tag records the freeze.
/// </summary>
/// <param name="ClientId">
/// The OAuth client the session was established with. A session is bound to the client as well as
/// the app, so rotating an app onto a new client invalidates it (task 3.6).
/// </param>
/// <param name="Ticket">
/// The serialized authentication ticket, as bytes. Never a live ticket object: the store would then
/// hand two requests the same mutable instance.
/// </param>
public sealed record SessionRecord(
    string AppId,
    string ClientId,
    string Subject,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long Version,
    ReadOnlyMemory<byte> Ticket);

/// <summary>
/// Where sessions live. In memory for the proof of concept (task 3.4), and behind the same interface
/// in Phase 2. Freezes with <see cref="SessionRecord"/> at G1b.
/// </summary>
public interface ISessionStore
{
    /// <summary>Stores a new session and returns its key.</summary>
    Task<string> CreateAsync(SessionRecord record, CancellationToken ct);

    /// <summary>Reads a session. Null means there is none under that key.</summary>
    Task<SessionRecord?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// Replaces a session only if it is still at <paramref name="expectedVersion"/>. False means it
    /// was not. The in-memory store implements this trivially; the Phase 2 store needs it for
    /// optimistic concurrency, and having it now means Phase 2 changes no caller.
    /// </summary>
    Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct);

    /// <summary>Removes one session. Removing a key that is not there is not an error.</summary>
    Task RemoveAsync(string key, CancellationToken ct);

    /// <summary>
    /// Removes every session for an app: the app left the registry, or it was rotated onto a new
    /// client (task 3.6). Must not touch another app's sessions.
    /// </summary>
    Task RemoveAppAsync(string appId, CancellationToken ct);
}
