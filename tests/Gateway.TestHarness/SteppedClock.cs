namespace Gateway.TestHarness;

/// <summary>
/// The system clock, put forward by <see cref="Advance"/>: both the wall clock and the monotonic one, by
/// the same amount, while timers still run on the system's. Given as <see cref="GatewayHarnessOptions.Clock"/>
/// it is the clock of every host, the test provider and the refresh-lease table, so a composed row moves
/// past a lease's time to live or the provider's grace window in one step, and a loser's poll and the
/// provider's delays still fire (#332 F3, #346).
///
/// Not a <c>FakeTimeProvider</c>, on purpose: the token library computes a new token's expiry from the
/// system clock (T-B10) and the token source compares it with this clock, so a clock standing at a fixed
/// instant would read every token as fresh or every one as due; and the mock API validates lifetimes on
/// the system clock. Put forward by seconds, a five-minute token is still fresh. Workstream 3's
/// <c>tests/Platform.Identity.Tests/SteppedClock.cs</c> is the same clock; a harness references no
/// <c>tests/*.Tests</c> project, so this is its own copy.
/// </summary>
public sealed class SteppedClock : TimeProvider
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
