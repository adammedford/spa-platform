using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// The behaviour every <see cref="IRefreshLeases"/> must have (change request P2-3 Decision item 6;
/// ADR-0024). Subclass it once per implementation and override <see cref="CreateLeases"/>: the
/// in-memory double here, and the DynamoDB lease, which change request P2-3 Decision item 7 proves in
/// workstream 3's project.
///
/// The suite hands the implementation the clock it then moves, so expiry is tested without waiting.
/// Every lease here lives a whole number of seconds and no case asserts the instant of expiry itself,
/// only well before and after it, because the DynamoDB lease keeps <c>leaseUntil</c> in epoch
/// milliseconds and takes over under <c>leaseUntil &lt; :now</c>.
///
/// What is deliberately not here: a same-token re-acquire. <see cref="IRefreshLeases.TryAcquireAsync"/>
/// takes no token, and a caller whose success response was lost never held one; that retry is
/// internal to the DynamoDB adapter and proved over its protocol fake, not here (G3 on #332). No case
/// needs a session store: the lease is independent of the record (ADR-0024 decision 3).
///
/// Each case names the mutation of an implementation that turns it red.
/// </summary>
public abstract class RefreshLeasesContract
{
    /// <summary>What a holder asks for: <c>ProviderDeadline + LeaseMargin</c>, 10 s + 11 s today.</summary>
    protected static readonly TimeSpan Ttl = TimeSpan.FromSeconds(21);

    /// <summary>The implementation under test, reading <paramref name="clock"/> for every expiry.</summary>
    protected abstract IRefreshLeases CreateLeases(TimeProvider clock);

    [Fact]
    public async Task A_free_key_can_be_taken()
    {
        // Mutation: answer null on every acquire, and this fails.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);

        RefreshLease? lease = await leases.TryAcquireAsync("session-a", Ttl, Ct);

        Assert.NotNull(lease);
        Assert.False(string.IsNullOrEmpty(lease.Token));
        Assert.Equal(TestClock.Instant + Ttl, lease.ExpiresAt);
    }

    [Fact]
    public async Task A_held_key_cannot_be_taken()
    {
        // Mutation: grant a held key (skip the held check), and the second acquire is not null.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        Assert.NotNull(await leases.TryAcquireAsync("session-a", Ttl, Ct));

        clock.Advance(Ttl - TimeSpan.FromSeconds(1));

        Assert.Null(await leases.TryAcquireAsync("session-a", Ttl, Ct));
    }

    [Fact]
    public async Task A_released_key_can_be_taken()
    {
        // Mutation: make a release change nothing, and the key stays held until it expires.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        RefreshLease first = (await leases.TryAcquireAsync("session-a", Ttl, Ct))!;

        await leases.ReleaseAsync("session-a", first, Ct);

        RefreshLease? second = await leases.TryAcquireAsync("session-a", Ttl, Ct);
        Assert.NotNull(second);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task An_expired_lease_can_be_taken_over()
    {
        // The holder that died mid-refresh (ADR-0024 decision 4). Mutation: never expire a lease, and
        // the takeover is refused.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        RefreshLease dead = (await leases.TryAcquireAsync("session-a", Ttl, Ct))!;

        clock.Advance(Ttl + TimeSpan.FromSeconds(1));

        RefreshLease? takeover = await leases.TryAcquireAsync("session-a", Ttl, Ct);
        Assert.NotNull(takeover);
        Assert.NotEqual(dead.Token, takeover.Token);
        Assert.Equal(clock.GetUtcNow() + Ttl, takeover.ExpiresAt);
    }

    [Fact]
    public async Task A_release_by_a_holder_whose_lease_was_taken_over_changes_nothing()
    {
        // The overrunning holder (ADR-0024 decision 5). Mutation: let a stale release free the key
        // (release by key, ignoring the token), and the successor's lease is freed under it.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        RefreshLease overran = (await leases.TryAcquireAsync("session-a", Ttl, Ct))!;
        clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        Assert.NotNull(await leases.TryAcquireAsync("session-a", Ttl, Ct));

        await leases.ReleaseAsync("session-a", overran, Ct);

        Assert.Null(await leases.TryAcquireAsync("session-a", Ttl, Ct));
    }

    [Fact]
    public async Task A_second_release_of_the_same_lease_changes_nothing()
    {
        // A token already released does not free the next holder. Mutation: let a stale release free
        // the key, and the next holder's lease is freed under it.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        RefreshLease first = (await leases.TryAcquireAsync("session-a", Ttl, Ct))!;
        await leases.ReleaseAsync("session-a", first, Ct);
        Assert.NotNull(await leases.TryAcquireAsync("session-a", Ttl, Ct));

        await leases.ReleaseAsync("session-a", first, Ct);

        Assert.Null(await leases.TryAcquireAsync("session-a", Ttl, Ct));
    }

    [Fact]
    public async Task Two_keys_are_independent()
    {
        // Mutation: share state across keys (one lease for every key), and the second key is refused
        // while the first is held, or freed when the first is released.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);
        RefreshLease a = (await leases.TryAcquireAsync("session-a", Ttl, Ct))!;

        RefreshLease? b = await leases.TryAcquireAsync("session-b", Ttl, Ct);
        Assert.NotNull(b);

        await leases.ReleaseAsync("session-a", a, Ct);
        Assert.Null(await leases.TryAcquireAsync("session-b", Ttl, Ct));
    }

    [Fact]
    public async Task A_key_with_no_session_record_can_be_taken_and_no_store_is_needed()
    {
        // No ISessionStore exists in this case, and none is created: the key below was never a
        // session (ADR-0024 decision 3). Mutation: require a session record under the key (the
        // DynamoDB store's attribute_exists(pk) on the session item), and this acquire is refused.
        FakeTimeProvider clock = TestClock.New();
        IRefreshLeases leases = CreateLeases(clock);

        Assert.NotNull(await leases.TryAcquireAsync("no-session-was-ever-created-under-this-key", Ttl, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
