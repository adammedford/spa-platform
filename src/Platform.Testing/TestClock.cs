using Microsoft.Extensions.Time.Testing;

namespace Platform.Testing;

/// <summary>
/// The fake clock every workstream's tests share. The clock is
/// <see cref="FakeTimeProvider"/>; what this adds is one starting instant, so a session that expires
/// "eight hours in" reads the same in any test project.
///
/// <code>
/// FakeTimeProvider clock = TestClock.New();
/// clock.Advance(TimeSpan.FromMinutes(4));   // one minute before a 5-minute access token expires
/// </code>
/// </summary>
public static class TestClock
{
    /// <summary>
    /// Where a fake clock starts: 09:00 UTC on 12 October 2026, a weekday morning. Access tokens
    /// live 5 minutes and sessions 8 hours, so both expiries land on the same day and read plainly.
    /// </summary>
    public static readonly DateTimeOffset Instant = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A clock at the shared instant.</summary>
    public static FakeTimeProvider New() => At(Instant);

    /// <summary>A clock at some other instant, for a test that needs one.</summary>
    public static FakeTimeProvider At(DateTimeOffset instant) => new(instant);
}
