using Platform.Abstractions;

namespace Gateway.TestHarness;

/// <summary>
/// One gateway host's way to the cell's one <see cref="SwitchableSessionStore"/>: every call goes through
/// to the shared records, and a hook runs before each versioned replace this host sends. The hook is where
/// a composed concurrency trace puts the other writer's turn (#1007, F-35), as workstream 3's
/// <c>ScriptedSessionStore</c> does for its two gateways (tests/Platform.Identity.Tests/FragmentTraceTests.cs),
/// so a trace's interleaving is fixed rather than raced. Only a harness host in cell mode has one
/// (<see cref="GatewayHost.SessionStore"/>).
/// </summary>
public sealed class HostSessionStore(ISessionStore shared) : ISessionStore
{
    private int replaces;

    /// <summary>The cell's one store, which every host's view reaches.</summary>
    public ISessionStore Shared => shared;

    /// <summary>
    /// Runs before each of this host's versioned replaces reaches the records, with the replace's number
    /// on this host, from 1. Null runs nothing.
    /// </summary>
    public Func<int, Task>? BeforeReplace { get; set; }

    /// <summary>Runs before each of this host's reads reaches the records. Null runs nothing.</summary>
    public Func<Task>? BeforeGet { get; set; }

    /// <summary>How many versioned replaces this host has sent.</summary>
    public int Replaces => Volatile.Read(ref replaces);

    /// <summary>
    /// The exception the DynamoDB store raises when an operation failed or its outcome is unknown at
    /// <c>StoreDeadline</c>, which every writer answers with 503 <c>session_store_unavailable</c> under
    /// <c>auth.store</c>. The type is workstream 3's own and internal, and only that store raises it, so a
    /// hook that throws it is the cell's DynamoDB table failing, as far as the gateway can tell; made by
    /// reflection because nothing outside that workstream may construct it.
    /// </summary>
    public static Exception Unavailable(string operation)
    {
        Type type = typeof(Platform.Identity.PlatformIdentityExtensions).Assembly
            .GetType("Platform.Identity.SessionStoreUnavailableException", throwOnError: true)!;
        return (Exception)Activator.CreateInstance(type, operation, TimeSpan.FromSeconds(5), null)!;
    }

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => shared.CreateAsync(record, ct);

    public async Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        if (BeforeGet is { } hook)
        {
            await hook();
        }

        return await shared.GetAsync(key, ct);
    }

    public async Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        int attempt = Interlocked.Increment(ref replaces);
        if (BeforeReplace is { } hook)
        {
            await hook(attempt);
        }

        return await shared.ReplaceAsync(key, record, expectedVersion, ct);
    }

    public Task RemoveAsync(string key, CancellationToken ct) => shared.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => shared.RemoveAppAsync(appId, ct);
}
