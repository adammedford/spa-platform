namespace Platform.Identity.Tests;

/// <summary>
/// A clock whose monotonic reading moves only when the test moves it, while the wall clock is the
/// system's put forward by the same amount and timers still run on the system's. The token source times
/// the lease budget's check on the monotonic clock, from the acquire's send to after the re-read
/// (change request P2-3, second amendment, 2026-10-29), so on this clock that interval reads exactly
/// the advances made inside the acquire and the re-read and nothing else. <see cref="SteppedClock"/>
/// adds the system's own monotonic time, which puts the boundary row (acquire and re-read together
/// taking exactly <c>StoreDeadline</c>) one real microsecond over the line on every run.
///
/// The wall clock stays the system's for the reason <see cref="SteppedClock"/> gives: the token
/// library computes a token's expiry from the system clock (T-B10), and the source compares it with
/// this clock. Timers stay the system's so a loser's poll fires without the test advancing anything.
/// </summary>
internal sealed class ManualMonotonicClock : TimeProvider
{
    private long offsetTicks;

    /// <summary>How far the test has put this clock forward.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    /// <summary>Puts both clocks forward by <paramref name="by"/>, to the tick.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

    /// <summary>One timestamp unit per <see cref="TimeSpan"/> tick, so an advance of one tick reads as one.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref offsetTicks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;
}
