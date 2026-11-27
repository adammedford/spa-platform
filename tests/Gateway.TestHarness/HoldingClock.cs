namespace Gateway.TestHarness;

/// <summary>
/// A clock for the test identity provider whose delays last until the test releases them, rather than
/// for the time they name: with <c>TokenDelay</c> set, a token request the provider is holding is a timer
/// here. <see cref="Holding"/> completes the moment the provider starts holding one, and nothing it holds
/// goes on until <see cref="Release"/>. The time it reads is <paramref name="basis"/>'s. Given as
/// <see cref="GatewayHarnessOptions.IdpClock"/>, so the gateway's own timers run while the provider holds
/// a refresh (#1007, F-35). Workstream 3's <c>tests/Platform.Identity.Tests/HeldClock.cs</c> is the same
/// clock; a harness references no <c>tests/*.Tests</c> project, so this is its own copy.
/// </summary>
public sealed class HoldingClock(TimeProvider basis) : TimeProvider
{
    /// <summary>How long a held delay waits for <see cref="Release"/> before it ends anyway, so a failed test does not hang the provider's shutdown.</summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the provider first starts a delay, which for a token request is the hold.</summary>
    public Task Holding => holding.Task;

    /// <summary>Lets every delay the provider is holding, or will start, end now.</summary>
    public void Release() => released.TrySetResult();

    public override DateTimeOffset GetUtcNow() => basis.GetUtcNow();

    public override long TimestampFrequency => basis.TimestampFrequency;

    public override long GetTimestamp() => basis.GetTimestamp();

    public override TimeZoneInfo LocalTimeZone => basis.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        holding.TrySetResult();
        return new HeldTimer(released.Task.WaitAsync(Backstop), callback, state);
    }

    /// <summary>A one-shot timer that fires when the release does, unless it was disposed first.</summary>
    private sealed class HeldTimer : ITimer
    {
        private int disposed;

        public HeldTimer(Task release, TimerCallback callback, object? state) =>
            _ = release.ContinueWith(
                _ =>
                {
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        callback(state);
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
