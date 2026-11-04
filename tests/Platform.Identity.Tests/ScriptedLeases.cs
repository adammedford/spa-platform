using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// One gateway's view of a lease table: every call goes to <paramref name="inner"/>, a
/// <c>Platform.Testing.InMemoryRefreshLeases</c> that two gateways may share, and is recorded, and a test
/// can script what one gateway's lease calls do around it: take time or pause after an acquisition, never
/// give a lease back (a holder that died), or fail a release.
/// </summary>
internal sealed class ScriptedLeases(IRefreshLeases inner) : IRefreshLeases
{
    private readonly ConcurrentQueue<TimeSpan> asked = new();
    private readonly ConcurrentQueue<RefreshLease> acquired = new();
    private readonly ConcurrentQueue<RefreshLease> released = new();
    private readonly TaskCompletionSource refused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releasedOne = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int attempts;

    public IRefreshLeases Inner { get; } = inner;

    /// <summary>Runs before each acquisition is sent, with its attempt number from 1: another task's work in between.</summary>
    public Func<int, Task>? BeforeAcquire { get; set; }

    /// <summary>Runs after each acquisition that took a lease, with its attempt number from 1: latency, or a pause.</summary>
    public Func<int, Task>? AfterAcquire { get; set; }

    /// <summary>A holder that died: its releases never reach the table.</summary>
    public bool KeepsLeases { get; set; }

    /// <summary>Runs as each release arrives, before it reaches the table: what was true when the lease was given back.</summary>
    public Action<RefreshLease>? OnRelease { get; set; }

    /// <summary>Thrown by every release once it has reached the table (or would have), when set.</summary>
    public Func<string, RefreshLease, Exception>? ReleaseFault { get; set; }

    /// <summary>Every time to live asked for, oldest first.</summary>
    public IReadOnlyList<TimeSpan> Asked => [.. asked];

    /// <summary>Every lease taken, oldest first.</summary>
    public IReadOnlyList<RefreshLease> Acquired => [.. acquired];

    /// <summary>Every lease given back, oldest first, whether or not it reached the table.</summary>
    public IReadOnlyList<RefreshLease> Released => [.. released];

    /// <summary>Completes on the first acquisition that found the lease held: this gateway is a loser.</summary>
    public Task FirstRefusal => refused.Task;

    /// <summary>Completes on the first release.</summary>
    public Task FirstRelease => releasedOne.Task;

    public async Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
    {
        asked.Enqueue(ttl);
        int attempt = Interlocked.Increment(ref attempts);
        if (BeforeAcquire is { } before)
        {
            await before(attempt);
        }

        RefreshLease? lease = await Inner.TryAcquireAsync(sessionKey, ttl, ct);
        if (lease is null)
        {
            refused.TrySetResult();
            return null;
        }

        acquired.Enqueue(lease);
        if (AfterAcquire is { } after)
        {
            await after(attempt);
        }

        return lease;
    }

    public async Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct)
    {
        released.Enqueue(lease);
        OnRelease?.Invoke(lease);
        try
        {
            if (!KeepsLeases)
            {
                await Inner.ReleaseAsync(sessionKey, lease, ct);
            }

            if (ReleaseFault is { } fault)
            {
                throw fault(sessionKey, lease);
            }
        }
        finally
        {
            releasedOne.TrySetResult();
        }
    }
}

/// <summary>
/// A session store whose next <see cref="ReplaceAsync"/> a test scripts: stalls until released (a holder
/// that died after the provider rotated and before the write), fails before committing, or commits and
/// then fails its answer (#332 F3, F3b). Every other call goes straight to <paramref name="inner"/>.
/// </summary>
internal sealed class ScriptedReplaceStore(ISessionStore inner) : ISessionStore
{
    private readonly TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum Fault
    {
        None,
        Stall,
        FailBeforeCommit,
        FailAfterCommit,
    }

    /// <summary>What the next replace does; back to <see cref="Fault.None"/> once it has been used.</summary>
    public Fault Next { get; set; }

    /// <summary>Completes when a stalled replace has begun to wait.</summary>
    public Task Stalled => stalled.Task;

    /// <summary>Lets a stalled replace go on to the store.</summary>
    public void Resume() => resume.TrySetResult();

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

    public async Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        Fault fault = Next;
        Next = Fault.None;
        switch (fault)
        {
            case Fault.Stall:
                stalled.TrySetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                return await inner.ReplaceAsync(key, record, expectedVersion, ct);
            case Fault.FailBeforeCommit:
                throw new IOException("The store failed before the replace committed.");
            case Fault.FailAfterCommit:
                await inner.ReplaceAsync(key, record, expectedVersion, ct);
                throw new IOException("The replace committed and its answer was lost.");
            default:
                return await inner.ReplaceAsync(key, record, expectedVersion, ct);
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);
}
