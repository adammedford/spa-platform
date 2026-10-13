using Microsoft.Extensions.Time.Testing;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The fake clock. The clock itself is <see cref="FakeTimeProvider"/> from
/// Microsoft.Extensions.TimeProvider.Testing, which is not this repository's to test. What is tested
/// here is the shared starting instant: sessions, token expiry and cache lifetimes are compared
/// across workstreams, and a test that invented its own "now" would be hard to read against one that
/// did not.
/// </summary>
public sealed class TestClockTests
{
    [Fact]
    public void A_clock_starts_at_the_shared_instant()
    {
        Assert.Equal(TestClock.Instant, TestClock.New().GetUtcNow());
    }

    [Fact]
    public void The_shared_instant_is_in_utc()
    {
        // A fake clock in a local offset makes an expiry comparison read differently per machine.
        Assert.Equal(TimeSpan.Zero, TestClock.Instant.Offset);
    }

    [Fact]
    public void A_clock_can_start_somewhere_else()
    {
        DateTimeOffset midnight = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(midnight, TestClock.At(midnight).GetUtcNow());
    }

    [Fact]
    public void Advancing_a_clock_moves_it_forward()
    {
        FakeTimeProvider clock = TestClock.New();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(TestClock.Instant.AddMinutes(5), clock.GetUtcNow());
    }
}
