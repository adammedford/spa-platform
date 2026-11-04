using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Two gateway tasks of one cell, as two <see cref="IdentityGateway"/>s over one
/// <see cref="InMemorySessionStore"/>, one <see cref="InMemoryRefreshLeases"/>, one test identity provider
/// and one key ring repository (change request P2-3, Decision item 7; #332 F3 to F6; #341). Workstream 6
/// proves the same rows again over the composed gateway.
///
/// Each gateway sees the shared lease table through a <see cref="ScriptedLeases"/> of its own, so a test
/// can make one of them a holder that died or paused. Time past a lease or a grace window is a
/// <see cref="SteppedClock"/> shared by both gateways, the lease table and the provider, put forward in
/// one step, so the figures are today's: a 21 s lease, a 10 s or 60 s grace.
/// </summary>
public sealed class TwoGatewayRefreshTests
{
    private static readonly AppDefinition App1 = RefreshLeaseTests.App1;

    [Fact]
    public async Task Fifty_calls_split_across_two_gateways_sharing_one_lease_table_make_one_grant_and_no_refusal()
    {
        // P2-3's acceptance and #332 F5, at grace zero, so a replay cannot be forgiven: one grant, no
        // refused grant, every call answered with that grant's token, and the record holding its
        // rotated refresh token. The control below is this test with two lease tables.
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, sharedLeases: true);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        (string key, _) = await cell.A.Sessions.SingleAsync();
        cell.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        cell.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);

        IdentityGateway.Answer[] answers = await FiftyAsync(cell, jar);

        TokenGrant grant = Assert.Single(cell.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        Assert.All(answers, a => Assert.Equal((200, grant.AccessToken), (a.Status, a.Body)));
        Assert.Equal(grant.RefreshTokenIssued, await RefreshLeaseTests.StoredTokenAsync(cell.Store, key, "refresh_token"));
    }

    [Fact]
    public async Task Fifty_calls_split_across_two_gateways_with_a_lease_table_each_make_two_grants_and_one_is_refused()
    {
        // The control: the same fifty calls with a lease table per gateway, which is two tasks sharing no
        // lease, as before P2-3. Each gateway's gate takes its own lease, both reach the provider with the
        // same refresh token, and at grace zero the second grant is refused. This is the mutation
        // "two lease tables", which turns the test above red.
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, sharedLeases: false);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        cell.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        cell.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);

        await FiftyAsync(cell, jar);

        Assert.Equal(2, cell.Idp.RefreshGrants.Count);
        Assert.Single(cell.Idp.RefreshGrants, g => g.Outcome == "invalid_grant");
    }

    [Fact]
    public async Task A_loser_on_the_other_gateway_reads_the_holders_token_instead_of_refreshing()
    {
        // Change request P2-3 item 4: while gateway A holds the lease and the provider holds its refresh,
        // gateway B's call cannot take the lease, re-reads, and answers A's token once A has written it.
        // Mutation: a loser that refreshes anyway (treat a refused acquisition as a held one); B makes a
        // second grant, and this fails.
        HeldClock hold = new();
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, sharedLeases: true, idpClock: hold);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        cell.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        Task<IdentityGateway.Answer> holder = Task.Run(() => cell.A.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cell.Idp.Faults.TokenDelay = TimeSpan.Zero;
        Task<IdentityGateway.Answer> loser = Task.Run(() => cell.B.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await cell.LeasesB.FirstRefusal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        hold.Release();
        IdentityGateway.Answer[] answers = await Task.WhenAll(holder, loser);

        TokenGrant grant = Assert.Single(cell.Idp.RefreshGrants);
        Assert.All(answers, a => Assert.Equal((200, grant.AccessToken), (a.Status, a.Body)));
        Assert.Empty(cell.LeasesB.Acquired);
    }

    [Fact]
    public async Task A_holder_reads_the_record_again_under_the_lease_and_uses_a_refresh_written_after_its_turn_began()
    {
        // Change request P2-3's order, lease then read then decide. Gateway A's turn reads a due token;
        // before A's acquisition is sent, gateway B takes the lease, refreshes, writes and gives it back.
        // A then holds a free lease over a record that has moved: it reads it again and answers B's token,
        // at grace zero with no grant of its own. Mutation: decide on the turn's first read (no re-read
        // under the lease); A refreshes with the token B rotated out, the provider refuses it, and the
        // "no refusal" assertion fails.
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, sharedLeases: true);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        cell.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        IdentityGateway.Answer? first = null;
        cell.LeasesA.BeforeAcquire = async attempt =>
        {
            if (attempt == 1)
            {
                first = await cell.B.SendAsync(App1, IdentityGateway.TokenPath, jar);
            }
        };

        IdentityGateway.Answer answer = await cell.A.SendAsync(App1, IdentityGateway.TokenPath, jar);

        TokenGrant grant = Assert.Single(cell.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        Assert.Equal((200, grant.AccessToken), (first?.Status, first?.Body));
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Single(cell.LeasesA.Released);
    }

    [Theory]
    [InlineData(60, 200)]
    [InlineData(10, 401)]
    public async Task A_holder_that_dies_after_the_provider_rotated_is_recovered_inside_the_grace_window_and_ends_once_after_it(
        int graceSeconds, int expected)
    {
        // #332 F3, both sides of the bound (TTL < t < r + G). Gateway A's refresh is granted, and A dies
        // before its write: the write never lands and its lease is never given back. Gateway B's call is a
        // loser until the 21 s lease lapses, then takes it over and refreshes with the superseded token.
        // Inside a 60 s grace the provider honours it and the session continues; past a 10 s grace it is
        // refused, the re-read finds the same token, the session ends once under one logged reason, and
        // the next request is challenged. The provider's answer delay alone cannot place the death: a
        // late answer either arrives, and A writes, or meets the deadline, and A gives the lease back
        // (T-D9), so the death is a write that stalls.
        // Mutation: a loser that never takes over an expired lease; B waits out its bound and answers
        // 503, and both rows fail.
        CapturedLog logsB = new();
        SteppedClock clock = new();
        await using Cell cell = await Cell.StartAsync(clock, sharedLeases: true, idpClock: clock, logsB: logsB);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        (string key, _) = await cell.A.Sessions.SingleAsync();
        cell.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(graceSeconds);
        cell.LeasesA.KeepsLeases = true;
        cell.StoreA.Next = ScriptedReplaceStore.Fault.Stall;

        Task<IdentityGateway.Answer> dead = Task.Run(() => cell.A.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await cell.StoreA.Stalled.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(Assert.Single(cell.Idp.RefreshGrants).Ok);

        Task<IdentityGateway.Answer> successor = Task.Run(() => cell.B.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await cell.LeasesB.FirstRefusal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(new AccessTokenSourceOptions().LeaseTimeToLive + TimeSpan.FromSeconds(1));
        IdentityGateway.Answer answer = await successor;

        Assert.Equal(expected, answer.Status);
        Assert.Equal(2, cell.Idp.RefreshGrants.Count);
        if (expected == 200)
        {
            TokenGrant retry = cell.Idp.RefreshGrants[1];
            Assert.True(retry is { Ok: true, ReplayedInsideGrace: true });
            Assert.Equal(retry.AccessToken, answer.Body);
            Assert.Equal(retry.RefreshTokenIssued, await RefreshLeaseTests.StoredTokenAsync(cell.Store, key, "refresh_token"));
        }
        else
        {
            Assert.Equal(("invalid_grant", "session_ended"), (cell.Idp.RefreshGrants[1].Outcome, answer.Body));
            Assert.Single(RefreshLeaseTests.SourceLines(logsB), line => line.Contains("has ended", StringComparison.Ordinal));
            Assert.Equal(302, (await cell.B.SendAsync(App1, "/", jar, headers: IdentityGateway.Navigation)).Status);
        }

        // The dead holder's write, let go at last, expects the version it read and loses to whatever B
        // left, so it never overwrites B's outcome.
        cell.StoreA.Resume();
        IdentityGateway.Answer late = await dead;
        Assert.NotEqual(cell.Idp.RefreshGrants[0].AccessToken, late.Body);
        Assert.Equal(2, cell.Idp.RefreshGrants.Count);
    }

    [Fact]
    public async Task A_holder_paused_past_its_lease_resumes_after_a_takeover_as_a_loser_and_answers_its_own_sessions_token()
    {
        // #332 F4 and D2. Gateway A takes the lease and pauses before its check, past the 21 s lease.
        // Gateway B takes the lapsed lease over and refreshes, and the provider holds B's refresh. A
        // resumes: too little of its lease is left, so it gives it back, which, fenced by its token,
        // leaves B's lease held; it never calls the provider, and once B has written it answers B's
        // token, which is the token of its own session and of no other.
        // Mutation: drop the pre-call check; A refreshes with the token B is rotating, a second grant is
        // made, and this fails. Mutation: release by key rather than by token (a release that ignores the
        // fence); B's lease is freed, and the "still held" assertion fails.
        HeldClock hold = new();
        SteppedClock clock = new();
        await using Cell cell = await Cell.StartAsync(clock, sharedLeases: true, idpClock: hold);
        Dictionary<string, string> other = await RefreshLeaseTests.LoginDueAsync(cell.A);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(cell.A);
        string key = cell.A.Sessions.Created[1];
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cell.LeasesA.AfterAcquire = async attempt =>
        {
            if (attempt == 1)
            {
                paused.TrySetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };

        Task<IdentityGateway.Answer> resumed = Task.Run(() => cell.A.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(new AccessTokenSourceOptions().LeaseTimeToLive + TimeSpan.FromSeconds(1));
        cell.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
        Task<IdentityGateway.Answer> takeover = Task.Run(() => cell.B.SendAsync(App1, IdentityGateway.TokenPath, jar), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cell.Idp.Faults.TokenDelay = TimeSpan.Zero;

        resume.TrySetResult();
        await cell.LeasesA.FirstRelease.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(await cell.Leases.TryAcquireAsync(key, TimeSpan.FromSeconds(1), Ct));
        Assert.Empty(cell.Idp.RefreshGrants);
        hold.Release();

        IdentityGateway.Answer a = await resumed;
        IdentityGateway.Answer b = await takeover;
        TokenGrant grant = Assert.Single(cell.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (b.Status, b.Body));
        Assert.Equal((200, grant.AccessToken), (a.Status, a.Body));
        Assert.Equal(a.Body, await RefreshLeaseTests.StoredTokenAsync(cell.Store, key, "access_token"));
        Assert.NotEqual(a.Body, await RefreshLeaseTests.StoredTokenAsync(cell.Store, cell.A.Sessions.Created[0], "access_token"));
        Assert.NotEmpty(other);
    }

    [Fact]
    public async Task A_logout_through_one_gateway_is_answered_unauthenticated_by_the_other_on_the_next_request()
    {
        // P2-3's second acceptance and #332 F6: B serves the session A created, then A logs it out, and
        // B's next request finds no session: 401 unauthenticated from the token path, since the session
        // is gone rather than refused on a refresh, and a challenge from the shell gate. Mutation: a
        // gateway that keeps a copy of each record it has read (a cache in AppSessions.ReadAsync, which
        // the cookie handler's ticket store reads through); B still serves the session after the logout,
        // and this fails.
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, sharedLeases: true);
        IdentityGateway.Login login = await cell.A.LoginAsync(App1);
        Assert.Equal(200, (await cell.B.SendAsync(App1, IdentityGateway.TokenPath, login.Jar)).Status);

        Assert.Equal(302, (await cell.A.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST")).Status);

        IdentityGateway.Answer next = await cell.B.SendAsync(App1, IdentityGateway.TokenPath, login.Jar);
        Assert.Equal((401, "unauthenticated"), (next.Status, next.Body));
        Assert.Equal(302, (await cell.B.SendAsync(App1, "/", login.Jar, headers: IdentityGateway.Navigation)).Status);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IdentityGateway.Answer[]> FiftyAsync(Cell cell, Dictionary<string, string> jar) =>
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(
            () => (i % 2 == 0 ? cell.A : cell.B).SendAsync(App1, IdentityGateway.TokenPath, jar), Ct)));

    /// <summary>
    /// Two gateways, A and B, over one store and one identity provider, and one lease table or one each.
    /// A logs in; B reads A's cookies through the key ring repository it shares.
    /// </summary>
    private sealed class Cell : IAsyncDisposable
    {
        private Cell(IdentityGateway a, IdentityGateway b, InMemorySessionStore store, ScriptedReplaceStore storeA,
            InMemoryRefreshLeases leases, ScriptedLeases leasesA, ScriptedLeases leasesB)
        {
            (A, B, Store, StoreA, Leases, LeasesA, LeasesB) = (a, b, store, storeA, leases, leasesA, leasesB);
        }

        public IdentityGateway A { get; }

        public IdentityGateway B { get; }

        public TestIdentityProvider Idp => A.Idp;

        /// <summary>The store both gateways share.</summary>
        public InMemorySessionStore Store { get; }

        /// <summary>A's way to the shared store, whose next replace a test can stall.</summary>
        public ScriptedReplaceStore StoreA { get; }

        /// <summary>The shared lease table (A's, when each has its own).</summary>
        public InMemoryRefreshLeases Leases { get; }

        public ScriptedLeases LeasesA { get; }

        public ScriptedLeases LeasesB { get; }

        public static async Task<Cell> StartAsync(
            TimeProvider clock, bool sharedLeases, TimeProvider? idpClock = null, CapturedLog? logsB = null)
        {
            InMemorySessionStore store = new();
            ScriptedReplaceStore storeA = new(store);
            InMemoryRefreshLeases leases = new(clock);
            ScriptedLeases leasesA = new(leases);
            ScriptedLeases leasesB = new(sharedLeases ? leases : new InMemoryRefreshLeases(clock));

            IdentityGateway a = await IdentityGateway.StartAsync(
                [App1], services: s => s.AddSingleton(clock), idpClock: idpClock,
                sessions: new RecordingSessionStore(storeA), leases: leasesA);
            IdentityGateway b = await IdentityGateway.StartAsync(
                [App1], services: s =>
                {
                    s.AddSingleton(clock);
                    if (logsB is not null)
                    {
                        s.AddSingleton(logsB.Provider);
                    }
                },
                keys: a.Keys, beside: a, sessions: new RecordingSessionStore(store), leases: leasesB);
            return new Cell(a, b, store, storeA, leases, leasesA, leasesB);
        }

        public async ValueTask DisposeAsync()
        {
            StoreA.Resume();
            await B.DisposeAsync();
            await A.DisposeAsync();
        }
    }
}
