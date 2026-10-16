using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// What plan row 3.4 gives the in-memory store beyond the shared contract suite: a session past its
/// expiry is gone, a sweep frees it without anyone asking for it, and <c>Session:MaxSessions</c>
/// bounds how many it holds (ADR-0004). Each runs on the shared fake clock, so nothing here waits.
/// </summary>
public sealed class MemorySessionStoreTests
{
    private static readonly TimeSpan EightHours = TimeSpan.FromHours(8);

    [Fact]
    public async Task A_session_past_its_expiry_reads_as_null()
    {
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 10, clock);
        string key = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        clock.Advance(EightHours);

        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_session_one_second_before_its_expiry_still_reads()
    {
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 10, clock);
        string key = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        clock.Advance(EightHours - TimeSpan.FromSeconds(1));

        Assert.NotNull(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_replace_of_an_expired_session_is_refused()
    {
        // The cookie handler renews on a read it has just made; a session that ended in between must
        // not come back to life under its old key.
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 10, clock);
        string key = await store.CreateAsync(Record(expiresIn: EightHours), Ct);
        clock.Advance(EightHours);

        Assert.False(await store.ReplaceAsync(key, Record(expiresIn: EightHours, version: 2), expectedVersion: 1, Ct));
        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task The_sweep_frees_expired_sessions_nobody_reads_and_keeps_the_live_ones()
    {
        // A user who closes the tab never reads their session again. Without a sweep it would hold
        // memory, and a place under the bound, until the process restarts.
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 10, clock);
        await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(1)), Ct);
        await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(1)), Ct);
        string live = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        clock.Advance(TimeSpan.FromHours(1) + MemorySessionStore.SweepInterval);

        Assert.Equal(1, store.Count);
        Assert.NotNull(await store.GetAsync(live, Ct));
    }

    [Fact]
    public async Task Nothing_is_swept_before_it_expires()
    {
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 10, clock);
        await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(1)), Ct);

        clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task At_capacity_a_new_session_evicts_the_one_nearest_its_expiry()
    {
        // Session:MaxSessions bounds the store (ADR-0004). A login at the bound still succeeds; the
        // session that gives up its place is the one with the least of its life left.
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 2, clock);
        string longer = await store.CreateAsync(Record(expiresIn: EightHours), Ct);
        string nearest = await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(2)), Ct);

        string newest = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        Assert.Equal(2, store.Count);
        Assert.Null(await store.GetAsync(nearest, Ct));
        Assert.NotNull(await store.GetAsync(longer, Ct));
        Assert.NotNull(await store.GetAsync(newest, Ct));
    }

    [Fact]
    public async Task At_capacity_an_expired_session_gives_up_its_place_before_any_live_one()
    {
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 2, clock);
        string live = await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(2)), Ct);
        // Expired before the first sweep reaches it, so only the bound can free its place.
        await store.CreateAsync(Record(expiresIn: TimeSpan.FromSeconds(10)), Ct);
        clock.Advance(TimeSpan.FromSeconds(10));

        string newest = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        Assert.Equal(2, store.Count);
        Assert.NotNull(await store.GetAsync(live, Ct));
        Assert.NotNull(await store.GetAsync(newest, Ct));
    }

    [Fact]
    public async Task A_replace_at_capacity_evicts_nothing()
    {
        // A refresh changes a session; it does not add one.
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 2, clock);
        string first = await store.CreateAsync(Record(expiresIn: TimeSpan.FromHours(2)), Ct);
        string second = await store.CreateAsync(Record(expiresIn: EightHours), Ct);

        Assert.True(await store.ReplaceAsync(second, Record(expiresIn: EightHours, version: 2), expectedVersion: 1, Ct));

        Assert.NotNull(await store.GetAsync(first, Ct));
        Assert.Equal(2, (await store.GetAsync(second, Ct))!.Version);
    }

    [Fact]
    public async Task Logins_at_once_never_take_the_store_past_its_bound()
    {
        FakeTimeProvider clock = TestClock.New();
        using MemorySessionStore store = new(maxSessions: 50, clock);

        string[] keys = await Task.WhenAll(Enumerable.Range(0, 1_000).Select(i =>
            Task.Run(() => store.CreateAsync(Record(expiresIn: EightHours + TimeSpan.FromSeconds(i)), Ct), Ct)));

        Assert.Equal(50, store.Count);
        Assert.Equal(1_000, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_bound_below_one_is_refused(int maxSessions)
    {
        // contracts/platform-config.schema.json: Session.MaxSessions has a minimum of 1.
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySessionStore(maxSessions, TestClock.New()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionRecord Record(TimeSpan expiresIn, string appId = "app1", long version = 1) => new(
        AppId: appId,
        ClientId: "1example23456789",
        Subject: "user-1",
        CreatedAt: TestClock.Instant,
        ExpiresAt: TestClock.Instant + expiresIn,
        Version: version,
        Ticket: new byte[] { 7, 7, 7 });
}
