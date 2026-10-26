namespace Platform.Identity.Tests;

/// <summary>
/// The system clock, put forward by <see cref="Advance"/>: both the wall clock and the monotonic one, by
/// the same amount, while timers still run on the system's. A test moves past a lease's time to live or
/// a provider's grace window in one step, and a loser's poll and the provider's delays still fire.
///
/// Not a <c>FakeTimeProvider</c>, on purpose: the token library computes a new token's expiry from the
/// system clock (T-B10), and the token source compares it with this clock, so a clock that stood still at
/// a fixed instant would read every token as fresh or every one as due. Put forward by seconds, a
/// five-minute token is still fresh and a thirty-second one still due.
/// </summary>
internal sealed class SteppedClock : TimeProvider
{
    private long offsetTicks;

    /// <summary>How far ahead of the system clock this one reads.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    /// <summary>Puts both clocks forward by <paramref name="by"/>.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

    public override long TimestampFrequency => System.TimestampFrequency;

    public override long GetTimestamp() =>
        System.GetTimestamp() + (long)(Offset.Ticks * ((double)System.TimestampFrequency / TimeSpan.TicksPerSecond));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;
}
