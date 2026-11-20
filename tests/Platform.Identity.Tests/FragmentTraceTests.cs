using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Traces 1 to 5 of #995 (the fragments spec #961's second part), each on two <see cref="IdentityGateway"/>s
/// over one session store, one lease table, one key ring and one test identity provider, as two tasks of
/// one cell. Each gateway reaches the shared store through a <see cref="ScriptedSessionStore"/> of its
/// own, whose hook before a versioned replace is where a trace puts the other writer's turn, so each
/// trace's interleaving is fixed rather than raced. Versions are stated relative to the record's version
/// after sign-in, which the trace text calls v7.
/// </summary>
public sealed class FragmentTraceTests
{
    private static readonly FragmentDefinition Orders = FragmentRefreshTests.Orders;
    private static readonly AppDefinition Shell = FragmentRefreshTests.Shell;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SessionRecord> RecordAsync(Cell cell, string key) =>
        Assert.IsType<SessionRecord>(await cell.Store.GetAsync(key, Ct));

    private static string? OrdersToken(SessionRecord record, string name) =>
        FragmentSets.Token(Held.Properties(record), Orders, name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trace_1_a_shell_refresh_and_a_fragment_refresh_of_one_record_both_win(bool pastTheWriteDeadline)
    {
        // A refreshes the shell's set and B refreshes orders', both reading v7. A wins v8 {S1, F0}; B's
        // replace loses, its re-read shows orders still holding F0, and it re-applies: v9 {S1, F1}, 200 on
        // both. Variant: B's second attempt would start with less than 2S before its acquire + 20 s, so B
        // answers its own F1 unpersisted and v8 keeps F0. Mutation: drop the re-apply (treat B's lost
        // replace as superseded); the first row's record keeps F0, and this fails (1 test red). Mutation:
        // no write deadline on a re-apply; the second row writes F1, and this fails (1 test red).
        SteppedClock clock = new();
        await using Cell cell = await Cell.StartAsync(clock);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(
            cell.A, lifetimeOf: client => client == FragmentRefreshTests.Billing.OAuth.ClientId ? FragmentRefreshTests.Fresh : FragmentRefreshTests.Due);
        long v7 = (await RecordAsync(cell, key)).Version;
        string f0 = OrdersToken(await RecordAsync(cell, key), "access_token")!;
        IdentityGateway.Answer? a = null;
        cell.StoreB.BeforeReplace = async (_, attempt) =>
        {
            if (attempt == 1)
            {
                a = await FragmentRefreshTests.TokenAsync(cell.A, jar);
                if (pastTheWriteDeadline)
                {
                    clock.Advance(TimeSpan.FromSeconds(11));
                }
            }
        };

        IdentityGateway.Answer b = await FragmentRefreshTests.TokenAsync(cell.B, jar, Orders);

        TokenGrant s1 = Assert.Single(cell.Idp.RefreshGrants, g => g.ClientId == Shell.OAuth.ClientId);
        TokenGrant f1 = Assert.Single(cell.Idp.RefreshGrants, g => g.ClientId == Orders.OAuth.ClientId);
        Assert.Equal((200, s1.AccessToken), (a?.Status, a?.Body));
        Assert.Equal((200, f1.AccessToken), (b.Status, b.Body));
        SessionRecord record = await RecordAsync(cell, key);
        Assert.Equal(s1.AccessToken, Held.Properties(record).GetTokenValue("access_token"));
        if (pastTheWriteDeadline)
        {
            Assert.Equal((v7 + 1, f0), (record.Version, OrdersToken(record, "access_token")));
        }
        else
        {
            Assert.Equal((v7 + 2, f1.AccessToken), (record.Version, OrdersToken(record, "access_token")));
        }
    }

    [Fact]
    public async Task Trace_2i_a_callback_that_writes_first_supersedes_a_fragment_refresh()
    {
        // B's fallback callback writes v8 {F'} first; A's refresh replace loses, its re-read shows F' (not
        // the F0 it presented), so A is superseded and drops F1. Winner: the callback; record v8; A answers
        // 200 with F''s token; B 302 to returnUrl. Mutation: re-apply whatever the winner holds; A writes
        // F1 over F', and this fails (1 test red).
        await using Cell cell = await Cell.StartAsync(TimeProvider.System);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        long v7 = (await RecordAsync(cell, key)).Version;
        IdentityGateway.Answer? callback = null;
        int signIn = cell.StoreA.Replaces;
        cell.StoreA.BeforeReplace = async (_, attempt) =>
        {
            if (attempt == signIn + 1)
            {
                callback = await FallbackAsync(cell.B, jar);
            }
        };

        IdentityGateway.Answer a = await FragmentRefreshTests.TokenAsync(cell.A, jar, Orders);

        SessionRecord record = await RecordAsync(cell, key);
        string fPrime = OrdersToken(record, "access_token")!;
        TokenGrant f1 = Assert.Single(cell.Idp.RefreshGrants);
        Assert.NotEqual(f1.AccessToken, fPrime);
        Assert.Equal(v7 + 1, record.Version);
        Assert.Equal((200, fPrime), (a.Status, a.Body));
        Assert.Equal((302, "/home"), (callback?.Status, callback?.Location));
    }

    [Fact]
    public async Task Trace_2ii_a_callback_that_loses_to_a_fragment_refresh_re_applies_its_set_unconditionally()
    {
        // A's refresh writes v8 {F1} first; B's callback replace loses and it re-applies F' unconditionally
        // within MergeBudget: v9 {F'}. Winner: the callback; A 200 (F1), B 302; F1 orphaned. Mutation: a
        // callback that drops its set on a lost replace (MergeBudget 1); the record keeps F1, and this
        // fails (1 test red).
        await using Cell cell = await Cell.StartAsync(TimeProvider.System);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        long v7 = (await RecordAsync(cell, key)).Version;
        IdentityGateway.Answer? a = null;
        cell.StoreB.BeforeReplace = async (_, attempt) =>
        {
            if (attempt == 1)
            {
                a = await FragmentRefreshTests.TokenAsync(cell.A, jar, Orders);
            }
        };

        IdentityGateway.Answer callback = await FallbackAsync(cell.B, jar);

        TokenGrant f1 = Assert.Single(cell.Idp.RefreshGrants);
        Assert.Equal((200, f1.AccessToken), (a?.Status, a?.Body));
        Assert.Equal((302, "/home"), (callback.Status, callback.Location));
        SessionRecord record = await RecordAsync(cell, key);
        Assert.Equal(v7 + 2, record.Version);
        string fPrime = OrdersToken(record, "access_token")!;
        Assert.NotEqual(f1.AccessToken, fPrime);
        Assert.Contains(fPrime, cell.Idp.IssuedAccessTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trace_3_a_logout_on_one_gateway_during_a_fragment_refresh_on_the_other_leaves_no_record_and_answers_unauthenticated(
        bool grantAfterRevocation)
    {
        // A refreshes orders from v7; B's logout revokes S0 and F0 and removes the record. Winner: logout;
        // no record; A answers 401 unauthenticated whether its grant came before the revocation (its write
        // finds the record gone) or after it (F0 is refused, and the refusal's re-read finds no record).
        // Mutation: a refusal whose bounded removal finds the record gone answers fragment_login_required;
        // the "after" row answers 401 fragment_login_required, and this fails (1 test red).
        HeldClock hold = new();
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, idpClock: grantAfterRevocation ? hold : null);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        AuthenticationProperties v7 = Held.Properties(await RecordAsync(cell, key));
        IdentityGateway.Answer? logout = null;
        Task<IdentityGateway.Answer> a;
        if (grantAfterRevocation)
        {
            cell.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
            a = Task.Run(() => FragmentRefreshTests.TokenAsync(cell.A, jar, Orders), Ct);
            await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cell.Idp.Faults.TokenDelay = TimeSpan.Zero;
            logout = await cell.B.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST");
            hold.Release();
        }
        else
        {
            int signIn = cell.StoreA.Replaces;
            cell.StoreA.BeforeReplace = async (_, attempt) =>
            {
                if (attempt == signIn + 1)
                {
                    logout = await cell.B.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST");
                }
            };
            a = FragmentRefreshTests.TokenAsync(cell.A, jar, Orders);
        }

        IdentityGateway.Answer answer = await a;

        Assert.Equal((401, "unauthenticated"), (answer.Status, answer.Body));
        Assert.Equal(302, logout?.Status);
        Assert.Null(await cell.Store.GetAsync(key, Ct));
        Assert.Contains(cell.Idp.Revocations, r => r.Token == v7.GetTokenValue("refresh_token"));
        Assert.Contains(cell.Idp.Revocations, r => r.Token == FragmentSets.Token(v7, Orders, "refresh_token"));
        Assert.Equal(grantAfterRevocation ? "invalid_grant" : "ok", Assert.Single(cell.Idp.RefreshGrants).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trace_4_a_lost_replace_whose_re_read_finds_the_record_gone_or_the_set_gone_writes_nothing(bool recordLives)
    {
        // A's replace loses (C wrote v8) and B's logout removes the record: A's one re-read finds nothing,
        // and A answers 401 unauthenticated. Variant: the record lives and C's write removed orders' set:
        // 401 fragment_login_required, and nothing is written. Mutation: on a re-read that finds the set
        // gone, re-apply it (resurrect the set); the variant's record gains orders again, and this fails
        // (1 test red).
        await using Cell cell = await Cell.StartAsync(TimeProvider.System);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        SessionRecord? written = null;
        int reads = 0;
        int signIn = cell.StoreA.Replaces;
        cell.StoreA.BeforeReplace = async (k, attempt) =>
        {
            if (attempt != signIn + 1)
            {
                return;
            }

            written = await WithoutOrdersAsync(cell, k);
            if (!recordLives)
            {
                Assert.Equal(302, (await cell.B.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST")).Status);
            }

            cell.StoreA.BeforeGet = _ =>
            {
                reads++;
                return Task.CompletedTask;
            };
        };

        IdentityGateway.Answer a = await FragmentRefreshTests.TokenAsync(cell.A, jar, Orders);

        Assert.Equal(1, reads);
        if (recordLives)
        {
            Assert.Equal((401, "fragment_login_required"), (a.Status, a.Body));
            Assert.Equal(written, await cell.Store.GetAsync(key, Ct));
        }
        else
        {
            Assert.Equal((401, "unauthenticated"), (a.Status, a.Body));
            Assert.Null(await cell.Store.GetAsync(key, Ct));
        }
    }

    [Fact]
    public async Task Trace_5_a_holder_whose_lease_lapsed_cannot_re_apply_past_its_write_deadline()
    {
        // A refreshes orders (F0 -> F1) and its first replace loses to C's write, which left F0 in place.
        // Before A re-applies, the clock passes A's acquire + 21 s: A's lease lapses and A2, on gateway B,
        // takes it over, presents F0 inside the 60 s grace and gets F2. A's write deadline (acquire + 20 s)
        // precludes its re-apply, so A answers F1 unpersisted and A2's write of F2 lands: the record holds a
        // live token. Trace 5's mutation, re-apply after the lease lapses (no write deadline on a re-apply),
        // writes F1 first; A2 is then superseded and drops F2, the record holds F1, and this fails (1 test
        // red).
        SteppedClock clock = new();
        await using Cell cell = await Cell.StartAsync(clock, idpClock: clock);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        cell.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(60);
        TaskCompletionSource a2Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource a2Write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IdentityGateway.Answer>? a2 = null;
        cell.StoreB.BeforeReplace = async (_, _) =>
        {
            a2Granted.TrySetResult();
            await a2Write.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        int signIn = cell.StoreA.Replaces;
        cell.StoreA.BeforeReplace = async (k, attempt) =>
        {
            if (attempt == signIn + 1)
            {
                await cell.StoreA.BumpAsync(k);
                clock.Advance(new AccessTokenSourceOptions().LeaseTimeToLive + TimeSpan.FromSeconds(1));
                a2 = Task.Run(() => FragmentRefreshTests.TokenAsync(cell.B, jar, Orders), Ct);
                await a2Granted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        IdentityGateway.Answer a = await FragmentRefreshTests.TokenAsync(cell.A, jar, Orders);
        a2Write.TrySetResult();
        IdentityGateway.Answer second = await a2!;

        Assert.Equal(2, cell.Idp.RefreshGrants.Count);
        TokenGrant f1 = cell.Idp.RefreshGrants[0];
        TokenGrant f2 = cell.Idp.RefreshGrants[1];
        Assert.True(f2 is { Ok: true, ReplayedInsideGrace: true });
        Assert.Equal((200, f1.AccessToken), (a.Status, a.Body));
        Assert.Equal((200, f2.AccessToken), (second.Status, second.Body));
        SessionRecord record = await RecordAsync(cell, key);
        Assert.Equal(f2.RefreshTokenIssued, OrdersToken(record, "refresh_token"));
        Assert.True(cell.Idp.IsRedeemable(OrdersToken(record, "refresh_token")));
    }

    [Fact]
    public async Task A_loser_on_a_fragments_lease_answers_fragment_login_required_once_the_holder_ended_the_set()
    {
        // #995 -> "Gates and leases per token set": A holds orders' lease while the provider holds its
        // refresh, and B's call on the other gateway waits as a loser. The provider then refuses orders'
        // token and A ends that set; B's next pass takes the free lease, finds no refresh token to present
        // and answers fragment_login_required with the record kept, making no grant, well before the
        // loser's bound. Mutation: end the record when a fragment's set has no refresh token (the shell's
        // rule); B answers session_ended, and this fails (1 test red).
        HeldClock hold = new();
        await using Cell cell = await Cell.StartAsync(TimeProvider.System, idpClock: hold);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(cell.A, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        cell.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
        Task<IdentityGateway.Answer> holder = Task.Run(() => FragmentRefreshTests.TokenAsync(cell.A, jar, Orders), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cell.Idp.Faults.TokenDelay = TimeSpan.Zero;
        Task<IdentityGateway.Answer> loser = Task.Run(() => FragmentRefreshTests.TokenAsync(cell.B, jar, Orders), Ct);
        await cell.LeasesB.FirstRefusal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cell.Idp.RevokeAllForClient(Orders.OAuth.ClientId);
        hold.Release();

        IdentityGateway.Answer a = await holder;
        IdentityGateway.Answer b = await loser.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal((401, "fragment_login_required"), (a.Status, a.Body));
        Assert.Equal((401, "fragment_login_required"), (b.Status, b.Body));
        Assert.Single(cell.Idp.RefreshGrants);
        Assert.Equal(FragmentSets.LoginRequired, FragmentSets.StateOf(Held.Properties(await RecordAsync(cell, key)), Orders));
    }

    /// <summary>
    /// <c>GET /bff/login?fragment=orders</c> on <paramref name="gateway"/> with the session's cookie: the
    /// fallback, whose callback merges a new orders set (F') into the record.
    /// </summary>
    private static async Task<IdentityGateway.Answer> FallbackAsync(IdentityGateway gateway, Dictionary<string, string> jar)
    {
        using FragmentBrowser browser = new(gateway, Shell);
        foreach ((string name, string value) in jar)
        {
            browser.Jar[name] = value;
        }

        return await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fhome");
    }

    /// <summary>C's write: the record one version on with orders' set removed.</summary>
    private static async Task<SessionRecord> WithoutOrdersAsync(Cell cell, string key)
    {
        SessionRecord current = await RecordAsync(cell, key);
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(current.Ticket.ToArray())!;
        FragmentSets.Remove(ticket.Properties, Orders.FragmentId);
        SessionRecord next = current with { Version = current.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await cell.Store.ReplaceAsync(key, next, current.Version, Ct));
        return next;
    }

    /// <summary>Two gateways, A and B, over one store, one lease table, one key ring and one provider. A signs in.</summary>
    private sealed class Cell : IAsyncDisposable
    {
        private Cell(IdentityGateway a, IdentityGateway b, InMemorySessionStore store, ScriptedSessionStore storeA, ScriptedSessionStore storeB, ScriptedLeases leasesB)
        {
            (A, B, Store, StoreA, StoreB, LeasesB) = (a, b, store, storeA, storeB, leasesB);
        }

        /// <summary>B's way to the shared lease table, which tells a test when B was first refused a lease.</summary>
        public ScriptedLeases LeasesB { get; }

        public IdentityGateway A { get; }

        public IdentityGateway B { get; }

        public TestIdentityProvider Idp => A.Idp;

        public InMemorySessionStore Store { get; }

        public ScriptedSessionStore StoreA { get; }

        public ScriptedSessionStore StoreB { get; }

        public static async Task<Cell> StartAsync(TimeProvider clock, TimeProvider? idpClock = null)
        {
            InMemorySessionStore store = new();
            ScriptedSessionStore storeA = new(store);
            ScriptedSessionStore storeB = new(store);
            InMemoryRefreshLeases leases = new(clock);
            ScriptedLeases leasesB = new(leases);
            IdentityGateway a = await IdentityGateway.StartAsync(
                [Shell], services: s => s.AddSingleton(clock), idpClock: idpClock, sessions: new RecordingSessionStore(storeA), leases: leases);
            IdentityGateway b = await IdentityGateway.StartAsync(
                [Shell], services: s => s.AddSingleton(clock), keys: a.Keys, beside: a, sessions: new RecordingSessionStore(storeB), leases: leasesB);
            return new Cell(a, b, store, storeA, storeB, leasesB);
        }

        public async ValueTask DisposeAsync()
        {
            await B.DisposeAsync();
            await A.DisposeAsync();
        }
    }
}
