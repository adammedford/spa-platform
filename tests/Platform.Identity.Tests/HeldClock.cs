namespace Platform.Identity.Tests;

/// <summary>
/// A clock for the test identity provider whose injected delays last until the test releases them,
/// rather than for the time they name. The provider runs every delay on its clock (TestIdpFaults), so
/// with <c>TokenDelay</c> set, a token request it is holding is a timer here: <see cref="Holding"/>
/// completes the moment the provider starts holding one, and nothing the provider is holding goes on
/// until <see cref="Release"/>.
///
/// It places a competing writer by time at the provider, while the provider holds this gateway's
/// refresh, and not by position in the gateway's reads of the session store (#167). Reading the time
/// is the system clock's, so token lifetimes and the grace window run as they would without it.
/// </summary>
internal sealed class HeldClock : TimeProvider
{
    /// <summary>
    /// How long a held delay waits for <see cref="Release"/> before it ends anyway, so a test that fails
    /// before releasing does not leave the provider holding a request through its shutdown.
    /// </summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the provider first starts a delay, which for a token request is the hold.</summary>
    public Task Holding => holding.Task;

    /// <summary>Lets every delay the provider is holding, or will start, end now.</summary>
    public void Release() => released.TrySetResult();

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
