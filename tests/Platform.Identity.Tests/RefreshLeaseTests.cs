using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// The refresh lease in <see cref="AccessTokenSource"/>, on one gateway over
/// <see cref="InMemoryRefreshLeases"/> (ADR-0024; change request P2-3, Decision items 3 to 5; #332 D2 and
/// F3b; #341). The lease table is wrapped in <see cref="ScriptedLeases"/>, so a test can see every lease
/// taken and given back and can make an acquisition take time. Two gateways sharing one table are
/// <see cref="TwoGatewayRefreshTests"/>.
/// </summary>
public sealed class RefreshLeaseTests
{
    internal static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    private static readonly TimeSpan Due = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    [Fact]
    public void The_margin_is_twice_the_store_deadline_and_a_second_and_the_time_to_live_follows_the_store_deadline()
    {
        // #332 G1: LeaseMargin = 2 × StoreDeadline + 1 s, so 10 s + 11 s = 21 s today, and the check's
        // lease budget is ProviderDeadline + StoreDeadline + 1 s = 16 s. Changing StoreDeadline moves the time
        // to live with it. Mutation: a fixed 11 s LeaseMargin; the 2 s StoreDeadline row reads 21 s, not
        // 15 s, and this fails.
        AccessTokenSourceOptions today = new();
        AccessTokenSourceOptions faster = new() { StoreDeadline = TimeSpan.FromSeconds(2) };

        Assert.Equal(
            (TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(16)),
            (today.LeaseMargin, today.LeaseTimeToLive, today.ReservedBudget));
        Assert.Equal(
            (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(13)),
            (faster.LeaseMargin, faster.LeaseTimeToLive, faster.ReservedBudget));
    }

    [Fact]
    public async Task The_lease_is_asked_for_the_derived_time_to_live()
    {
        // Change request P2-3 item 3: the time to live is the one derived value ProviderDeadline +
        // LeaseMargin, read here off the acquisition the token source made with StoreDeadline changed.
        // Mutation: ask for ProviderDeadline alone, or for a fixed 21 s; the lease is asked for 10 s or
        // 21 s, not 15 s, and this fails.
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            services: s => s.AddSingleton(new AccessTokenSourceOptions { StoreDeadline = TimeSpan.FromSeconds(2) }));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);

        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar)).Status);

        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(leases.Asked));
    }

    [Fact]
    public void The_pre_call_check_passes_at_its_boundary_and_a_lease_shorter_than_the_lease_budget_is_refused()
    {
        // #332 D2: "at least" the lease budget, ProviderDeadline + StoreDeadline + 1 s, must remain, so an
        // acquire and re-read that together spent the whole StoreDeadline pass, and one tick more does
        // not. A 15 s lease, the figure before G1, leaves less than the 16 s budget even at zero latency,
        // so the options are refused rather than left to release and retake the lease until the loser's
        // bound. The same boundary through the token source, with the time split between the acquire and
        // the re-read, is the test below on ManualMonotonicClock.
        // Mutation: compare with ">" instead of ">="; the boundary row fails.
        AccessTokenSourceOptions options = new();

        Assert.True(options.MayCallProvider(TimeSpan.Zero));
        Assert.True(options.MayCallProvider(options.StoreDeadline));
        Assert.False(options.MayCallProvider(options.StoreDeadline + TimeSpan.FromTicks(1)));
        Assert.Throws<InvalidOperationException>(() => new AccessTokenSourceOptions { LeaseMargin = TimeSpan.FromSeconds(5) }.Validated());
    }

    [Fact]
    public async Task A_holder_whose_acquisition_took_less_than_the_store_deadline_calls_the_provider()
    {
        // #341: an acquisition that takes 4.5 s of its 5 s StoreDeadline still leaves the 16 s lease budget
        // on a 21 s lease, so the holder refreshes. The latency is placed on the monotonic clock by putting it
        // forward inside the acquisition. Mutation: the 15 s lease (LeaseMargin = 5 s); the options are
        // refused, no token source is built, and this fails.
        SteppedClock clock = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(clock))
        {
            AfterAcquire = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(4.5));
                return Task.CompletedTask;
            },
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            services: s => s.AddSingleton<TimeProvider>(clock));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Single(leases.Acquired);
    }

    [Fact]
    public async Task A_holder_left_with_less_than_the_lease_budget_gives_the_lease_back_without_calling_the_provider()
    {
        // #332 D2: an acquisition that took 6 s leaves 15 s of a 21 s lease, under the 16 s lease budget, so
        // the holder gives the lease back before any grant, says so, and waits as a loser. Its next
        // acquisition is quick, and it refreshes then. Mutation: drop the check (always call the
        // provider); the grant is made before the release, and this fails.
        CapturedLog logs = new();
        SteppedClock clock = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(clock))
        {
            AfterAcquire = attempt =>
            {
                if (attempt == 1)
                {
                    clock.Advance(TimeSpan.FromSeconds(6));
                }

                return Task.CompletedTask;
            },
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            services: s => s.AddSingleton<TimeProvider>(clock).AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        int grantsAtTheRelease = -1;
        leases.OnRelease = _ => grantsAtTheRelease = grantsAtTheRelease < 0 ? gateway.Idp.RefreshGrants.Count : grantsAtTheRelease;

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(0, grantsAtTheRelease);
        Assert.Equal(leases.Acquired[0], leases.Released[0]);
        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Contains(SourceLines(logs), line => line.Contains("Too little of the refresh lease", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 200, 1)]
    [InlineData(1, 200, 2)]
    public async Task The_acquire_and_the_reread_together_are_admitted_at_the_store_deadline_and_refused_one_tick_over(
        int extraTicks, int expected, int acquisitions)
    {
        // Change request P2-3's second amendment, decision 1 (#560): the check is timed from the acquire's
        // send and read after the re-read under the lease, so the two together may take at most
        // StoreDeadline. Here each takes half of it: at exactly 5 s the holder is admitted and refreshes on
        // its first lease; one tick over, it gives the lease back before any grant, waits as a loser, and
        // refreshes on a second, quick lease. Mutation: start the clock after the acquire, or check before
        // the re-read; the one-tick row is then admitted on its first lease, and its acquisition count fails.
        ManualMonotonicClock clock = new();
        ScriptedReadStore store = new(new InMemorySessionStore());
        TimeSpan half = new AccessTokenSourceOptions().StoreDeadline / 2;
        ScriptedLeases leases = new(new InMemoryRefreshLeases(clock))
        {
            AfterAcquire = attempt =>
            {
                if (attempt == 1)
                {
                    clock.Advance(half);
                    store.OnNextGet = () =>
                    {
                        clock.Advance(half + TimeSpan.FromTicks(extraTicks));
                        return Task.CompletedTask;
                    };
                }

                return Task.CompletedTask;
            },
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            sessions: new RecordingSessionStore(store), services: s => s.AddSingleton<TimeProvider>(clock));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        int grantsAtTheRelease = -1;
        leases.OnRelease = _ => grantsAtTheRelease = grantsAtTheRelease < 0 ? gateway.Idp.RefreshGrants.Count : grantsAtTheRelease;

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((expected, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Equal(acquisitions, leases.Acquired.Count);
        Assert.Equal(leases.Acquired, leases.Released);
        Assert.Equal(acquisitions == 1 ? 1 : 0, grantsAtTheRelease);
    }

    [Fact]
    public async Task A_store_too_slow_for_any_pass_fails_the_call_after_twice_the_time_to_live_with_the_session_kept()
    {
        // Decision 1's exhaustion: every pass's acquire and re-read take 3 s each, inside their own 5 s
        // deadline and over it together, so every pass fails the check and gives the lease back, and once
        // an unsuccessful pass ends after 2 × TTL (42 s) has elapsed the call answers ProviderUnavailable
        // with no grant and the record as it was. 2 × TTL is the threshold a pass is checked against, not a
        // request deadline, so the call ends on the first pass to finish past it. Mutation: take the
        // threshold as a deadline on the acquire; the pass that straddles it ends on a cancelled store
        // call, not on this answer, and this fails.
        CapturedLog logs = new();
        ManualMonotonicClock clock = new();
        ScriptedReadStore store = new(new InMemorySessionStore());
        AccessTokenSourceOptions options = new();
        TimeSpan slow = TimeSpan.FromSeconds(3);
        ScriptedLeases leases = new(new InMemoryRefreshLeases(clock))
        {
            AfterAcquire = _ =>
            {
                clock.Advance(slow);
                store.OnNextGet = () =>
                {
                    clock.Advance(slow);
                    return Task.CompletedTask;
                };
                return Task.CompletedTask;
            },
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            sessions: new RecordingSessionStore(store), services: s => s.AddSingleton<TimeProvider>(clock).AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();
        TimeSpan started = clock.Offset;

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (answer.Status, answer.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Equal(atLogin, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Empty(answer.Cookies);
        Assert.Equal(leases.Acquired, leases.Released);
        TimeSpan elapsed = clock.Offset - started;
        Assert.True(elapsed > options.LeaseTimeToLive * 2, $"the call gave up after {elapsed}");
        Assert.True(elapsed <= (options.LeaseTimeToLive * 2) + slow + slow, $"the call went on past one pass over the threshold, to {elapsed}");
        Assert.Equal(leases.Acquired.Count, SourceLines(logs).Count(line => line.Contains("Too little of the refresh lease", StringComparison.Ordinal)));
        string warning = Assert.Single(SourceLines(logs), line => line.Contains("within two times to live", StringComparison.Ordinal));
        Assert.Contains(SessionRef.Of(key), warning, StringComparison.Ordinal);
        Assert.All(logs.Lines, line => Assert.DoesNotContain(key, line.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("refreshed")]
    [InlineData("unavailable")]
    [InlineData("ended")]
    public async Task The_lease_is_given_back_on_every_outcome(string outcome)
    {
        // Change request P2-3 item 5: release in a finally, on Refreshed, Unavailable and Ended alike, so
        // the next caller on any task can take the lease at once. Mutation: release only after a
        // successful write (no finally); the unavailable and ended rows keep the lease, and they fail.
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases);
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        (string key, _) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefreshOutageStatus = outcome == "unavailable" ? 503 : null;
        gateway.Idp.Faults.RefuseRefresh = outcome == "ended";

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(outcome switch { "refreshed" => 200, "unavailable" => 503, _ => 401 }, answer.Status);
        Assert.Equal(Assert.Single(leases.Acquired), Assert.Single(leases.Released));
        Assert.NotNull(await leases.Inner.TryAcquireAsync(key, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_lease_token_or_session_key_reaches_a_log_line_even_when_the_release_fails()
    {
        // Change request P2-3 item 5 and N-16: a failed release is a Warning by outcome, and neither the
        // lease's token nor the session key is on it, though the store's exception message carries both.
        // Mutation: log the exception (or the lease record, whose ToString prints its token); the token
        // reaches the line, and this fails.
        CapturedLog logs = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System))
        {
            ReleaseFault = (key, lease) => new IOException($"release of {key} with {lease.Token} failed"),
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            services: s => s.AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(200, answer.Status);
        string token = Assert.Single(leases.Acquired).Token;
        string warning = Assert.Single(SourceLines(logs), line => line.Contains("was not given back", StringComparison.Ordinal));
        Assert.Contains("(refreshed)", warning, StringComparison.Ordinal);
        Assert.Contains(SessionRef.Of(key), warning, StringComparison.Ordinal);
        Assert.All(logs.Lines, line =>
        {
            Assert.DoesNotContain(token, line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(key, line.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over_and_the_refresh_made()
    {
        // Change request P2-3 item 4, F3's recovery path: a holder that died keeps the lease until its
        // time to live; the loser re-reads, finds the session unmoved, and once the lease lapses takes it
        // over and refreshes. Mutation: a loser that only re-reads and never asks for the lease again; it
        // waits out its bound and answers 503, and this fails.
        SteppedClock clock = new();
        InMemoryRefreshLeases table = new(clock);
        ScriptedLeases leases = new(table);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            services: s => s.AddSingleton<TimeProvider>(clock));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        (string key, _) = await gateway.Sessions.SingleAsync();
        AccessTokenSourceOptions options = new();
        Assert.NotNull(await table.TryAcquireAsync(key, options.LeaseTimeToLive, TestContext.Current.CancellationToken));

        Task<IdentityGateway.Answer> call = Task.Run(
            () => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar), TestContext.Current.CancellationToken);
        await leases.FirstRefusal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(gateway.Idp.RefreshGrants);
        clock.Advance(options.LeaseTimeToLive + TimeSpan.FromSeconds(1));
        IdentityGateway.Answer answer = await call;

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Equal(grant.RefreshTokenIssued, await StoredTokenAsync(gateway.Sessions, key, "refresh_token"));
    }

    [Theory]
    [InlineData(60, 0, 200)]
    [InlineData(10, 11, 401)]
    public async Task A_replace_that_fails_before_committing_after_rotation_gives_the_lease_back_and_the_next_refresh_decides(
        int graceSeconds, int laterSeconds, int expected)
    {
        // #332 F3b, G2, not committed: the provider rotated, the write failed, and the call ends on the
        // store's exception (F17's path). The lease is given back, so the next refresh is made at once
        // with the superseded token: inside rotation + grace it is honoured and the session kept; after
        // it, it is refused, the re-read finds the same token, and the session ends once.
        // Mutation: release outside a finally (only after a refresh that returned); the lease is still
        // held after the failed call, and both rows fail.
        CapturedLog logs = new();
        SteppedClock clock = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(clock));
        ScriptedReplaceStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases, idpClock: clock,
            sessions: new RecordingSessionStore(store),
            services: s => s.AddSingleton<TimeProvider>(clock).AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        (string key, _) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(graceSeconds);
        store.Next = ScriptedReplaceStore.Fault.FailBeforeCommit;

        await Assert.ThrowsAsync<IOException>(() => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar));
        Assert.Equal(Assert.Single(leases.Acquired), Assert.Single(leases.Released));

        clock.Advance(TimeSpan.FromSeconds(laterSeconds));
        IdentityGateway.Answer next = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(expected, next.Status);
        Assert.Equal(2, gateway.Idp.RefreshGrants.Count);
        if (expected == 200)
        {
            Assert.True(gateway.Idp.RefreshGrants[1] is { Ok: true, ReplayedInsideGrace: true });
            Assert.Equal(gateway.Idp.RefreshGrants[1].AccessToken, next.Body);
        }
        else
        {
            Assert.Equal(("invalid_grant", "session_ended"), (gateway.Idp.RefreshGrants[1].Outcome, next.Body));
            Assert.Single(SourceLines(logs), line => line.Contains("has ended", StringComparison.Ordinal));
            Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_replace_that_commits_and_then_fails_gives_the_lease_back_and_the_next_request_uses_the_committed_token()
    {
        // #332 F3b, G2, committed: the write landed and its answer was lost, so the call ends on the
        // store's exception, the lease is given back, and the next request reads the committed token with
        // no new grant. Mutation: release outside a finally (only after a refresh that returned); the
        // lease is still held after the failed call, and the release assertion fails.
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        ScriptedReplaceStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            sessions: new RecordingSessionStore(store));
        Dictionary<string, string> jar = await LoginDueAsync(gateway);
        store.Next = ScriptedReplaceStore.Fault.FailAfterCommit;

        await Assert.ThrowsAsync<IOException>(() => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar));
        Assert.Equal(Assert.Single(leases.Acquired), Assert.Single(leases.Released));

        IdentityGateway.Answer next = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (next.Status, next.Body));
        Assert.Single(leases.Acquired);
    }

    /// <summary>
    /// A login while the provider issues 30-second access tokens, so the session's token is due at once,
    /// after which the provider goes back to five-minute tokens.
    /// </summary>
    internal static async Task<Dictionary<string, string>> LoginDueAsync(IdentityGateway gateway, AppDefinition? app = null)
    {
        gateway.Idp.Faults.AccessTokenLifetime = Due;
        IdentityGateway.Login login = await gateway.LoginAsync(app ?? App1);
        gateway.Idp.Faults.AccessTokenLifetime = Fresh;
        return login.Jar;
    }

    /// <summary>A token saved in the session under <paramref name="key"/>.</summary>
    internal static async Task<string> StoredTokenAsync(ISessionStore store, string key, string name)
    {
        SessionRecord record = Assert.IsType<SessionRecord>(await store.GetAsync(key, TestContext.Current.CancellationToken));
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        return Assert.IsType<string>(ticket.Properties.GetTokenValue(name));
    }

    /// <summary>The lines the token source itself wrote, oldest first.</summary>
    internal static string[] SourceLines(CapturedLog logs) =>
        [.. logs.Lines.Where(line => line.Category == typeof(AccessTokenSource).FullName).Select(line => line.Text)];
}
