using System.Collections.Concurrent;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>The bucket as one spactl run reaches it, recording every key it wrote.</summary>
internal sealed class WriteRecorder(IObjectStore inner) : IObjectStore
{
    private readonly ConcurrentQueue<string> written = new();

    /// <summary>Every key a write succeeded for, in order.</summary>
    public IReadOnlyList<string> Written => [.. written];

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => inner.GetAsync(key, ifNoneMatch, ct);

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        bool done = await inner.PutAsync(key, body, ifMatch, ct);
        if (done)
        {
            written.Enqueue(key);
        }

        return done;
    }
}

/// <summary>
/// Holds every claim write until <paramref name="parties"/> publishes have reached one, then lets them
/// race: F-T2's barrier before the claim (#961's traces comment, section 3), over the harness's bucket.
/// </summary>
internal sealed class ClaimBarrier(IObjectStore inner, int parties) : IObjectStore
{
    private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    /// <summary>How many claim writes reached the barrier.</summary>
    public int Arrived => Volatile.Read(ref arrived);

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => inner.GetAsync(key, ifNoneMatch, ct);

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        if (key.Contains("/claims/", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref arrived) >= parties)
            {
                all.TrySetResult();
            }

            await all.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        return await inner.PutAsync(key, body, ifMatch, ct);
    }
}
