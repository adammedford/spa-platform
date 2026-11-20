using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Per-token-set refresh (#995; the fragments spec #961 -> "Expiry", "Token selection and the 401", "Gates
/// and leases per token set" and "Writes after a lost versioned replace"), at
/// <see cref="IAccessTokenSource"/>'s seam: the token probe of <see cref="IdentityGateway"/>, on a shell's
/// route and on a fragment's, which pins <see cref="IFragmentFeature"/> as host resolution does. Sessions
/// are signed in through the whole chain against <c>fixtures/TestIdp</c>, each set's lifetime chosen per
/// client.
/// </summary>
public sealed class FragmentRefreshTests
{
    internal static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    internal static readonly FragmentDefinition Billing = FragmentChainTests.Billing;
    internal static readonly AppDefinition Shell = FragmentChainTests.Shell(Orders, Billing);

    internal static readonly TimeSpan Due = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A whole sign-in on <paramref name="shell"/>, the shell's login and every fragment's hop, with each
    /// client's access tokens issued for <paramref name="lifetimeOf"/> its client id (five minutes when
    /// null). Returns the browser's jar and the session's key.
    /// </summary>
    internal static async Task<(Dictionary<string, string> Jar, string Key)> SignInAsync(
        IdentityGateway gateway, AppDefinition? shell = null, Func<string, TimeSpan>? lifetimeOf = null)
    {
        shell ??= Shell;
        using FragmentBrowser browser = new(gateway, shell);
        browser.BeforeAuthorize = uri =>
        {
            string client = QueryHelpers.ParseQuery(uri.Query)["client_id"].ToString();
            gateway.Idp.Faults.AccessTokenLifetime = lifetimeOf?.Invoke(client) ?? Fresh;
            return Task.CompletedTask;
        };

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?returnUrl=%2Fhome");
        gateway.Idp.Faults.AccessTokenLifetime = Fresh;
        Assert.Equal((302, "/home"), (end.Status, end.Location));
        return (new Dictionary<string, string>(browser.Jar, StringComparer.Ordinal), gateway.Sessions.Created[^1]);
    }

    /// <summary>Every set due: the shell's and each fragment's.</summary>
    internal static Func<string, TimeSpan> AllDue => _ => Due;

    /// <summary>Only <paramref name="fragment"/>'s set due.</summary>
    internal static Func<string, TimeSpan> OnlyDue(FragmentDefinition fragment) =>
        client => client == fragment.OAuth.ClientId ? Due : Fresh;

    internal static Task<IdentityGateway.Answer> TokenAsync(
        IdentityGateway gateway, Dictionary<string, string> jar, FragmentDefinition? fragment = null, AppDefinition? shell = null) =>
        gateway.SendAsync(shell ?? Shell, IdentityGateway.TokenPath, jar, fragment: fragment);

    internal static async Task<AuthenticationProperties> PropertiesAsync(ISessionStore store, string key) =>
        Held.Properties(Assert.IsType<SessionRecord>(await store.GetAsync(key, Ct)));

    /// <summary>The items of a fragment's set as stored, its recorded client included: the S2 test's subject.</summary>
    internal static Dictionary<string, string?> FragmentItems(AuthenticationProperties properties, string fragmentId) =>
        properties.Items
            .Where(i => i.Key.EndsWith(":" + fragmentId, StringComparison.Ordinal))
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    [Fact]
    public async Task On_a_fragment_route_the_answer_is_that_fragments_token_and_never_the_shells()
    {
        // #995 -> "Token selection": GetAsync reads IFragmentFeature. Mutation: ignore the feature (answer
        // the shell's set on every route); the orders route answers the shell's token, and this fails
        // (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway);
        AuthenticationProperties held = await PropertiesAsync(gateway.Sessions, key);

        IdentityGateway.Answer shell = await TokenAsync(gateway, jar);
        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);
        IdentityGateway.Answer billing = await TokenAsync(gateway, jar, Billing);

        Assert.Equal((200, held.GetTokenValue("access_token")), (shell.Status, shell.Body));
        Assert.Equal((200, held.Items[FragmentSets.TokenItem("access_token", "orders")]), (orders.Status, orders.Body));
        Assert.Equal((200, held.Items[FragmentSets.TokenItem("access_token", "billing")]), (billing.Status, billing.Body));
        Assert.Equal(3, new[] { shell.Body, orders.Body, billing.Body }.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(gateway.Idp.RefreshGrants);
    }

    [Fact]
    public async Task With_no_usable_fragment_set_the_answer_is_fragment_login_required_and_with_no_record_unauthenticated()
    {
        // #995 -> "Token selection and the 401": a live record without the fragment's set answers
        // FragmentLoginRequired, writes nothing and asks the lease table for nothing; no record at all stays
        // Unauthenticated. Mutation: drop the absent-set check before the lease; the call takes
        // {sessionKey}:orders' lease before answering, and this fails (1 test red).
        KeyedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], leases: leases);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway);
        SessionRecord before = (await gateway.Sessions.GetAsync(key, Ct))!;
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(before.Ticket.ToArray())!;
        FragmentSets.Remove(ticket.Properties, "orders");
        SessionRecord without = before with { Version = before.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, without, before.Version, Ct));

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.Equal(without, await gateway.Sessions.GetAsync(key, Ct));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Empty(leases.Keys);

        await gateway.Sessions.RemoveAsync(key, Ct);
        IdentityGateway.Answer gone = await TokenAsync(gateway, jar, Orders);
        Assert.Equal((401, "unauthenticated"), (gone.Status, gone.Body));
    }

    [Fact]
    public async Task A_set_recorded_to_a_client_the_entry_no_longer_names_reads_as_absent_and_is_not_refreshed()
    {
        // #995 -> "Expiry": a fragment rotated onto a new client mid-session; its old set is never sent for
        // the new client. Mutation: read the set whatever client it was recorded to; the old token is
        // answered, and this fails (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, _) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        FragmentDefinition rotated = Orders with { OAuth = Orders.OAuth with { ClientId = "orders-client-2", ClientSecretRef = "orders-secret-2" } };
        AppDefinition moved = FragmentChainTests.Shell(rotated, Billing);
        await gateway.SwapAsync(moved);

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, rotated, moved);

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
    }

    [Fact]
    public async Task S2_a_refresh_of_the_shells_set_leaves_every_fragment_sets_suffixed_items_and_client_unchanged()
    {
        // The S2 regression test (#995): the shell's set refreshed through the real refresh path, its
        // StoreTokens rewrite and a TicketSerializer round trip, keeps each fragment's suffixed items and
        // recorded clientId byte for byte. It is the one test that reads the ticket's internal names,
        // because that persistence is its subject. Mutation: rewrite the shell's set by clearing every
        // ".Token." item first; the fragments' items are lost, and this fails (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: client => client == Shell.OAuth.ClientId ? Due : Fresh);
        AuthenticationProperties before = await PropertiesAsync(gateway.Sessions, key);

        IdentityGateway.Answer shell = await TokenAsync(gateway, jar);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal(Shell.OAuth.ClientId, grant.ClientId);
        Assert.Equal((200, grant.AccessToken), (shell.Status, shell.Body));
        AuthenticationProperties after = await PropertiesAsync(gateway.Sessions, key);
        Assert.Equal(grant.AccessToken, after.GetTokenValue("access_token"));
        foreach (FragmentDefinition fragment in new[] { Orders, Billing })
        {
            Dictionary<string, string?> items = FragmentItems(before, fragment.FragmentId);
            Assert.Contains(".Fragment.Client:" + fragment.FragmentId, items.Keys);
            Assert.Contains(FragmentSets.TokenItem("refresh_token", fragment.FragmentId), items.Keys);
            Assert.Equal(items, FragmentItems(after, fragment.FragmentId));
        }
    }

    [Fact]
    public async Task A_due_fragment_set_refreshes_with_its_own_client_and_refresh_token_and_leaves_the_shells_set_alone()
    {
        // The fragment's scheme carries its client (#994), so its refresh authenticates as that client and
        // presents that set's refresh token; the write touches that set alone. Mutation: refresh through the
        // shell's scheme; the grant names the shell's client, and this fails (1 test red).
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], s => s.AddSingleton(logs.Provider));
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        AuthenticationProperties before = await PropertiesAsync(gateway.Sessions, key);

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal(Orders.OAuth.ClientId, grant.ClientId);
        Assert.Equal(before.Items[FragmentSets.TokenItem("refresh_token", "orders")], grant.RefreshTokenUsed);
        Assert.Equal((200, grant.AccessToken), (orders.Status, orders.Body));
        AuthenticationProperties after = await PropertiesAsync(gateway.Sessions, key);
        Assert.Equal(grant.AccessToken, after.Items[FragmentSets.TokenItem("access_token", "orders")]);
        Assert.Equal(grant.RefreshTokenIssued, after.Items[FragmentSets.TokenItem("refresh_token", "orders")]);
        Assert.Equal(before.GetTokenValue("access_token"), after.GetTokenValue("access_token"));
        Assert.Equal(before.GetTokenValue("refresh_token"), after.GetTokenValue("refresh_token"));
        Assert.Equal(FragmentItems(before, "billing"), FragmentItems(after, "billing"));
        Assert.DoesNotContain(FragmentSets.TokenItem("id_token", "orders"), after.Items.Keys);

        string line = Assert.Single(RefreshLeaseTests.SourceLines(logs), l => l.StartsWith("Refresh write", StringComparison.Ordinal));
        Assert.Contains("fragment orders", line, StringComparison.Ordinal);
        Assert.Contains("ended written after 1 versioned replaces", line, StringComparison.Ordinal);
        Assert.Contains(SessionRef.Of(key), line, StringComparison.Ordinal);
        Assert.DoesNotContain(key, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fragments_invalid_grant_ends_only_its_set_and_answers_fragment_login_required()
    {
        // #995 -> "Expiry" and the W3 invalid_grant row: the provider refuses the orders client's refresh
        // tokens. The orders set is removed (a versioned replace, only while it held the presented token);
        // the record, the shell's set, billing's set and the cookie stay, and the shell's next call is
        // served. Mutation: end the record on a fragment's refusal (the shell's rule); the record is gone,
        // and this fails (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        AuthenticationProperties before = await PropertiesAsync(gateway.Sessions, key);
        gateway.Idp.RevokeAllForClient(Orders.OAuth.ClientId);

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.Equal("invalid_grant", Assert.Single(gateway.Idp.RefreshGrants).Outcome);
        Assert.DoesNotContain(orders.Cookies, c => c.Name == CookieOptionsSetup.CookieName);
        AuthenticationProperties after = await PropertiesAsync(gateway.Sessions, key);
        Assert.Empty(FragmentItems(after, "orders"));
        Assert.Equal(FragmentSets.LoginRequired, FragmentSets.StateOf(after, Orders));
        Assert.Equal(FragmentItems(before, "billing"), FragmentItems(after, "billing"));
        Assert.Equal(before.GetTokenValue("access_token"), after.GetTokenValue("access_token"));
        Assert.Equal(200, (await TokenAsync(gateway, jar)).Status);
        Assert.Equal(200, (await TokenAsync(gateway, jar, Billing)).Status);
        IdentityGateway.Answer again = await TokenAsync(gateway, jar, Orders);
        Assert.Equal((401, "fragment_login_required"), (again.Status, again.Body));
        Assert.Single(gateway.Idp.RefreshGrants);
    }

    [Fact]
    public async Task A_fragments_refusal_leaves_a_set_another_writer_replaced_in_the_meantime()
    {
        // The W3 invalid_grant row's CAS: the provider refuses the token orders' refresh presented, and
        // meanwhile another writer replaced orders' set with one holding another refresh token and, here,
        // no access token to hand back. The removal is made only while the set still holds the presented
        // token, so the other writer's set stays; the call answers fragment_login_required. Mutation: remove
        // the set unconditionally; the other writer's set is gone, and this fails (1 test red).
        HeldClock hold = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], idpClock: hold);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
        Task<IdentityGateway.Answer> call = Task.Run(() => TokenAsync(gateway, jar, Orders), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gateway.Idp.Faults.TokenDelay = TimeSpan.Zero;
        SessionRecord before = (await gateway.Sessions.GetAsync(key, Ct))!;
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(before.Ticket.ToArray())!;
        ticket.Properties.Items[FragmentSets.TokenItem("refresh_token", "orders")] = "another-writers-refresh-token";
        ticket.Properties.Items.Remove(FragmentSets.TokenItem("access_token", "orders"));
        SessionRecord other = before with { Version = before.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, other, before.Version, Ct));
        gateway.Idp.RevokeAllForClient(Orders.OAuth.ClientId);
        hold.Release();

        IdentityGateway.Answer orders = await call;

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.Equal("invalid_grant", Assert.Single(gateway.Idp.RefreshGrants).Outcome);
        Assert.Equal(other, await gateway.Sessions.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_fragments_refusal_answers_the_successors_token_when_another_writer_refreshed_the_set()
    {
        // The W3 invalid_grant row's first clause: the re-read finds orders' set holding a different refresh
        // token with an access token, so another writer refreshed it in between, and its access token is the
        // answer; nothing is removed. Mutation: skip the successor check (end the set on every refusal); the
        // call answers 401 and the successor's set is removed, and this fails (1 test red).
        HeldClock hold = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], idpClock: hold);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
        Task<IdentityGateway.Answer> call = Task.Run(() => TokenAsync(gateway, jar, Orders), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gateway.Idp.Faults.TokenDelay = TimeSpan.Zero;
        SessionRecord before = (await gateway.Sessions.GetAsync(key, Ct))!;
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(before.Ticket.ToArray())!;
        ticket.Properties.Items[FragmentSets.TokenItem("refresh_token", "orders")] = "successors-refresh-token";
        ticket.Properties.Items[FragmentSets.TokenItem("access_token", "orders")] = "successors-access-token";
        SessionRecord successor = before with { Version = before.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, successor, before.Version, Ct));
        gateway.Idp.RevokeAllForClient(Orders.OAuth.ClientId);
        hold.Release();

        IdentityGateway.Answer orders = await call;

        Assert.Equal((200, "successors-access-token"), (orders.Status, orders.Body));
        Assert.Equal("invalid_grant", Assert.Single(gateway.Idp.RefreshGrants).Outcome);
        Assert.Equal(successor, await gateway.Sessions.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_fragments_refusal_whose_removal_runs_out_of_attempts_answers_the_same_401()
    {
        // The W3 invalid_grant row: "same 401 if removal exhausted". Every removal replace loses to a writer
        // that keeps the presented token, so after three attempts the set is still there, the call answers
        // fragment_login_required, and the line counts three replaces. Mutation: answer an exhausted removal
        // as a provider outage (503); this fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        gateway.Idp.RevokeAllForClient(Orders.OAuth.ClientId);
        store.BeforeReplace = (k, _) => store.BumpAsync(k);

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.NotEmpty(FragmentItems(await PropertiesAsync(store, key), "orders"));
        Assert.Contains(RefreshLeaseTests.SourceLines(logs), l => l.Contains("fragment orders", StringComparison.Ordinal)
            && l.Contains("ended exhausted after 3 versioned replaces", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fragment_set_with_no_refresh_token_does_not_end_the_record()
    {
        // #995 -> "Expiry": a due fragment set that holds no refresh token answers fragment_login_required,
        // asks the provider nothing and writes nothing. Mutation: end the record when a set has no refresh
        // token (the shell's rule); the record is gone, and this fails (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        SessionRecord before = (await gateway.Sessions.GetAsync(key, Ct))!;
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(before.Ticket.ToArray())!;
        ticket.Properties.Items.Remove(FragmentSets.TokenItem("refresh_token", "orders"));
        SessionRecord without = before with { Version = before.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, without, before.Version, Ct));

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((401, "fragment_login_required"), (orders.Status, orders.Body));
        Assert.Equal(without, await gateway.Sessions.GetAsync(key, Ct));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Equal(200, (await TokenAsync(gateway, jar)).Status);
    }

    [Fact]
    public async Task A_fragments_provider_failure_other_than_invalid_grant_writes_nothing_and_answers_idp_unavailable()
    {
        // The W3 "other provider failure" row. Mutation: treat every failed refresh as invalid_grant; the
        // set is removed and the answer is 401, and this fails (1 test red).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        SessionRecord before = (await gateway.Sessions.GetAsync(key, Ct))!;
        gateway.Idp.Faults.RefreshOutageStatus = 503;

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((503, "idp_unavailable"), (orders.Status, orders.Body));
        Assert.Equal(before, await gateway.Sessions.GetAsync(key, Ct));
    }

    [Fact]
    public async Task Two_due_sets_of_one_record_refresh_in_parallel_each_under_its_own_gate_and_lease_key()
    {
        // #995 -> "Gates and leases per token set": the shell's set and orders' set are both due, and the
        // provider holds each refresh for a second. Each takes its own lease, under its own key (the
        // session key for the shell, {sessionKey}:orders for the fragment), both are held at once, and both
        // writes land in the one record. Mutation: key the fragment's lease by the session key (one lease
        // per record); the second refresh waits as a loser, the two leases are never held together, and
        // this fails (1 test red).
        KeyedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], leases: leases);
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: client => client == Billing.OAuth.ClientId ? Fresh : Due);
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        IdentityGateway.Answer[] answers = await Task.WhenAll(
            Task.Run(() => TokenAsync(gateway, jar), Ct),
            Task.Run(() => TokenAsync(gateway, jar, Orders), Ct));

        Assert.All(answers, a => Assert.Equal(200, a.Status));
        Assert.Equal([key, key + ":orders"], leases.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, leases.MostHeldAtOnce);
        Assert.Equal(2, gateway.Idp.RefreshGrants.Count(g => g.Ok));
        AuthenticationProperties after = await PropertiesAsync(gateway.Sessions, key);
        Assert.Equal(answers[0].Body, after.GetTokenValue("access_token"));
        Assert.Equal(answers[1].Body, after.Items[FragmentSets.TokenItem("access_token", "orders")]);
    }

    [Fact]
    public async Task A_store_failure_in_a_fragment_refresh_answers_503_session_store_unavailable()
    {
        // The W3 "unknown" row: the versioned replace after a granted refresh throws. The call answers 503
        // session_store_unavailable under auth.store, and the event line counts the attempt it made, with
        // outcome unconfirmed. Mutation: swallow the store failure and answer the unpersisted token; the
        // answer is 200, and this fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, _) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        store.BeforeReplace = (_, _) => throw ScriptedSessionStore.Unavailable("ReplaceAsync");

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((503, "session_store_unavailable"), (orders.Status, orders.Body));
        Assert.Equal(RuleIds.AuthStore, orders.Rule);
        Assert.Contains(RefreshLeaseTests.SourceLines(logs), l => l.Contains("fragment orders", StringComparison.Ordinal)
            && l.Contains("ended unconfirmed after 1 versioned replaces", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retried_write_records_its_attempt_count_on_the_refresh_event_line()
    {
        // G9 O2 evidence (Q35): the refresh's versioned replace loses twice to another writer that leaves
        // the set holding the presented refresh token, and the third lands. The one event line says
        // "written after 3 versioned replaces". Mutation: count only the winning replace (reset the count
        // on each re-apply); the line says 1, and this fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        int bumps = 0;
        store.BeforeReplace = async (k, _) =>
        {
            if (++bumps <= 2)
            {
                await store.BumpAsync(k);
            }
        };

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (orders.Status, orders.Body));
        Assert.Equal(grant.AccessToken, (await PropertiesAsync(store, key)).Items[FragmentSets.TokenItem("access_token", "orders")]);
        string line = Assert.Single(RefreshLeaseTests.SourceLines(logs), l => l.StartsWith("Refresh write", StringComparison.Ordinal));
        Assert.Contains("ended written after 3 versioned replaces", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_that_loses_every_attempt_answers_its_own_unpersisted_token_with_a_warning()
    {
        // The W3 "exhausted" row: every replace loses to a writer that keeps the presented refresh token,
        // so the refresh re-applies until its three attempts are spent, then answers its own token, which
        // was never persisted, with a Warning naming the count. Mutation: a fourth attempt (budget 4); the
        // write lands, and this fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        int replaces = 0;
        store.BeforeReplace = async (k, _) =>
        {
            if (++replaces <= 3)
            {
                await store.BumpAsync(k);
            }
        };

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (orders.Status, orders.Body));
        Assert.NotEqual(grant.AccessToken, (await PropertiesAsync(store, key)).Items[FragmentSets.TokenItem("access_token", "orders")]);
        Assert.Equal(3, replaces);
        Assert.Contains(logs.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && l.Message.Contains("fragment orders", StringComparison.Ordinal)
            && l.Message.Contains("after 3 versioned replaces", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refresh_whose_record_is_gone_on_the_re_read_writes_nothing_and_answers_unauthenticated()
    {
        // The W3 "conflict, gone" row and Trace 4's stop: the replace loses and the re-read finds no
        // record, so nothing is resurrected and nothing more is sent. Mutation: keep re-applying on a null
        // re-read; the call sends more replaces and answers its token, and this fails (1 test red).
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await SignInAsync(gateway, lifetimeOf: OnlyDue(Orders));
        store.BeforeReplace = (k, _) => store.Inner.RemoveAsync(k, Ct);
        int signIn = store.Replaces;

        IdentityGateway.Answer orders = await TokenAsync(gateway, jar, Orders);

        Assert.Equal((401, "unauthenticated"), (orders.Status, orders.Body));
        Assert.Null(await store.GetAsync(key, Ct));
        Assert.Equal(signIn + 1, store.Replaces);
    }

    /// <summary>
    /// A lease table that records the key of every lease it grants and how many it held at once: the
    /// observable of "one lease per token set".
    /// </summary>
    internal sealed class KeyedLeases(IRefreshLeases inner) : IRefreshLeases
    {
        private readonly ConcurrentQueue<string> keys = new();
        private int held;
        private int most;

        public IReadOnlyList<string> Keys => [.. keys];

        public int MostHeldAtOnce => Volatile.Read(ref most);

        public async Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
        {
            RefreshLease? lease = await inner.TryAcquireAsync(sessionKey, ttl, ct);
            if (lease is not null)
            {
                keys.Enqueue(sessionKey);
                int now = Interlocked.Increment(ref held);
                int seen;
                while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
                {
                }
            }

            return lease;
        }

        public async Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct)
        {
            Interlocked.Decrement(ref held);
            await inner.ReleaseAsync(sessionKey, lease, ct);
        }
    }
}
