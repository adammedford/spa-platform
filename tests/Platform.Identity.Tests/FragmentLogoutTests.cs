using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// One logout for the shell and every fragment (#995; the fragments spec #961 -> "Logout"), at
/// <c>POST /bff/logout</c>'s seam against <c>fixtures/TestIdp</c>: the gates, the re-read, a revocation
/// per set through its own scheme, one line per set, the removal and one provider logout by the shell's
/// client.
/// </summary>
public sealed class FragmentLogoutTests
{
    private static readonly FragmentDefinition Orders = FragmentRefreshTests.Orders;
    private static readonly FragmentDefinition Billing = FragmentRefreshTests.Billing;
    private static readonly AppDefinition Shell = FragmentRefreshTests.Shell;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] LogoutLines(CapturedLog logs) =>
        [.. logs.Lines.Where(l => l.Category == typeof(SessionEnd).FullName).Select(l => l.Text)];

    private static string LineFor(CapturedLog logs, string? fragmentId) => Assert.Single(LogoutLines(logs), l =>
        fragmentId is null ? !l.Contains("fragment", StringComparison.Ordinal) : l.Contains("fragment " + fragmentId + ":", StringComparison.Ordinal));

    [Theory]
    [InlineData(ClientCredentialStyle.PostBody, ClientAuth.Post)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader, ClientAuth.Basic)]
    public async Task Logout_revokes_every_set_through_its_own_client_logs_one_line_each_removes_the_record_then_signs_out_by_the_shells_client(
        ClientCredentialStyle style, ClientAuth expected)
    {
        // #995's logout acceptance: three sets, three revocations, each by its own client and in the
        // profile's credential style, one line per set with outcome revoked, the record removed, and one
        // provider logout by the shell's client with the shell's ID token. Mutation: revoke a fragment's
        // set through the shell's scheme; the provider records the shell's client for it (and refuses the
        // token as not its own), and this fails (2 tests red).
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], s => s.AddSingleton(logs.Provider));
        gateway.Profile.ClientCredentialStyle = style;
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway);
        AuthenticationProperties held = await FragmentRefreshTests.PropertiesAsync(gateway.Sessions, key);

        IdentityGateway.Answer logout = await gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST");

        Dictionary<string, Revocation> byClient = gateway.Idp.Revocations.ToDictionary(r => r.ClientId, StringComparer.Ordinal);
        Assert.Equal(3, byClient.Count);
        Assert.Equal(held.GetTokenValue("refresh_token"), byClient[Shell.OAuth.ClientId].Token);
        Assert.Equal(held.Items[FragmentSets.TokenItem("refresh_token", "orders")], byClient[Orders.OAuth.ClientId].Token);
        Assert.Equal(held.Items[FragmentSets.TokenItem("refresh_token", "billing")], byClient[Billing.OAuth.ClientId].Token);
        Assert.All(byClient.Values, r => Assert.True(r is { Known: true, Revoked: > 0 }));
        Assert.All(byClient.Values, r => Assert.Equal(expected, r.Auth));

        Assert.Equal(3, LogoutLines(logs).Length);
        Assert.EndsWith("revoking its refresh token: revoked.", LineFor(logs, null), StringComparison.Ordinal);
        Assert.EndsWith("of fragment orders: revoked.", LineFor(logs, "orders"), StringComparison.Ordinal);
        Assert.EndsWith("of fragment billing: revoked.", LineFor(logs, "billing"), StringComparison.Ordinal);
        Assert.All(LogoutLines(logs), l => Assert.Contains(SessionRef.Of(key), l, StringComparison.Ordinal));

        Assert.Null(await gateway.Sessions.GetAsync(key, Ct));
        Assert.Equal(302, logout.Status);
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
        Dictionary<string, string> query = QueryHelpers.ParseQuery(new Uri(logout.Location).Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        Assert.Equal(held.GetTokenValue("id_token"), query["id_token_hint"]);
        Assert.False(query.ContainsKey("client_id") && query["client_id"] != Shell.OAuth.ClientId);
    }

    [Fact]
    public async Task A_set_whose_fragment_was_removed_logs_no_scheme_and_one_recorded_to_a_changed_client_logs_client_changed()
    {
        // #995: tokens no current credential can revoke are dropped unrevoked and logged. Billing leaves the
        // entry and orders moves to a new client mid-session; only the shell's set is revoked, and the
        // record is still removed. Mutation: revoke a set whose recorded client differs, through the
        // fragment's current scheme; a revocation by the new client is made, and this fails (1 test red).
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], s => s.AddSingleton(logs.Provider));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway);
        FragmentDefinition rotated = Orders with { OAuth = Orders.OAuth with { ClientId = "orders-client-2", ClientSecretRef = "orders-secret-2" } };
        AppDefinition moved = FragmentChainTests.Shell(rotated);
        await gateway.SwapAsync(moved);

        IdentityGateway.Answer logout = await gateway.SendAsync(moved, IdentityGateway.LogoutPath, jar, "POST");

        Assert.Equal(302, logout.Status);
        Assert.Equal(Shell.OAuth.ClientId, Assert.Single(gateway.Idp.Revocations).ClientId);
        Assert.EndsWith(": revoked.", LineFor(logs, null), StringComparison.Ordinal);
        Assert.EndsWith("of fragment orders: client_changed.", LineFor(logs, "orders"), StringComparison.Ordinal);
        Assert.EndsWith("of fragment billing: no_scheme.", LineFor(logs, "billing"), StringComparison.Ordinal);
        Assert.Null(await gateway.Sessions.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_refresh_holding_a_fragments_gate_when_logout_starts_is_waited_for_then_its_set_is_revoked_and_removed()
    {
        // #995 and Trace 3: the provider holds orders' refresh, so its gate is held when logout starts.
        // Logout enters the shell's gate, waits on orders', and only once the refresh has written does it
        // re-read, revoke the token the refresh wrote and remove the record, well inside the 40 s bound.
        // Mutation: take no fragment gate (the shell's alone); logout revokes the superseded token before
        // the refresh writes, and this fails (1 test red).
        HeldClock hold = new();
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], s => s.AddSingleton(logs.Provider), idpClock: hold);
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        Task<IdentityGateway.Answer> refresh = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar, Orders), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gateway.Idp.Faults.TokenDelay = TimeSpan.Zero;
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<IdentityGateway.Answer> logout = Task.Run(() => gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST"), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.False(logout.IsCompleted);
        Assert.Empty(gateway.Idp.Revocations);
        hold.Release();

        IdentityGateway.Answer refreshed = await refresh;
        IdentityGateway.Answer loggedOut = await logout;
        elapsed.Stop();

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (refreshed.Status, refreshed.Body));
        Assert.Equal(302, loggedOut.Status);
        Assert.Equal(grant.RefreshTokenIssued, Assert.Single(gateway.Idp.Revocations, r => r.ClientId == Orders.OAuth.ClientId).Token);
        Assert.Null(await gateway.Sessions.GetAsync(key, Ct));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(40), $"logout took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task A_holder_stuck_past_the_gate_budget_is_skipped_as_gate_timeout_and_its_late_write_stops_on_the_null_re_read()
    {
        // #995: orders' refresh is granted and then stalls in its versioned replace, holding orders' gate,
        // past LogoutGateBudget (shrunk here: ProviderDeadline 3 s, StoreDeadline 0.5 s, so the budget is
        // the 4 s write deadline). Logout skips that gate, logs orders gate_timeout and revokes nothing for
        // it, revokes the others and removes the record. The stalled write then loses, re-reads nothing and
        // stops: no record is resurrected and the call answers 401 unauthenticated. Mutation: wait on every
        // gate with no budget; logout never returns while the write is stalled, and this fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        AccessTokenSourceOptions small = new() { ProviderDeadline = TimeSpan.FromSeconds(3), StoreDeadline = TimeSpan.FromMilliseconds(500) };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider).AddSingleton(small), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: FragmentRefreshTests.OnlyDue(Orders));
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeReplace = async (_, _) =>
        {
            stalled.TrySetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        Task<IdentityGateway.Answer> holder = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar, Orders), Ct);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Stopwatch elapsed = Stopwatch.StartNew();
        IdentityGateway.Answer logout = await gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST").WaitAsync(TimeSpan.FromSeconds(15), Ct);
        elapsed.Stop();

        Assert.Equal(302, logout.Status);
        Assert.True(elapsed.Elapsed >= small.LogoutGateBudget - TimeSpan.FromMilliseconds(100), $"logout returned after {elapsed.Elapsed}");
        Assert.EndsWith("of fragment orders: gate_timeout.", LineFor(logs, "orders"), StringComparison.Ordinal);
        Assert.EndsWith(": revoked.", LineFor(logs, null), StringComparison.Ordinal);
        Assert.EndsWith("of fragment billing: revoked.", LineFor(logs, "billing"), StringComparison.Ordinal);
        Assert.DoesNotContain(gateway.Idp.Revocations, r => r.ClientId == Orders.OAuth.ClientId);
        Assert.Null(await store.Inner.GetAsync(key, Ct));

        resume.TrySetResult();
        IdentityGateway.Answer late = await holder;
        Assert.Equal((401, "unauthenticated"), (late.Status, late.Body));
        Assert.Null(await store.Inner.GetAsync(key, Ct));
        // The withdrawn waiter left no gate behind once the holder let go.
        Assert.Equal(0, gateway.Services.GetRequiredService<AppSessions>().ActiveGates);
    }

    [Fact]
    public async Task A_shell_refusal_that_ends_the_record_while_logout_waits_enters_no_fragment_gate()
    {
        // #995 -> "Logout", step 1: "A shell invalid_grant enters no further gate." Orders' refresh is stalled
        // in its write, holding orders' gate; the shell's refresh is held at the provider, which then refuses
        // it, so the record ends while logout waits on the shell's gate. Logout then enters no fragment gate,
        // so it does not wait on orders' stalled holder, and returns at once. Mutation: enter every fragment
        // gate after a shell refusal; logout waits out the rest of its 4 s budget on orders' gate, and this
        // fails (1 test red).
        HeldClock hold = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        AccessTokenSourceOptions small = new() { ProviderDeadline = TimeSpan.FromSeconds(3), StoreDeadline = TimeSpan.FromMilliseconds(500) };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(small), idpClock: hold, sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: client => client == Billing.OAuth.ClientId ? FragmentRefreshTests.Fresh : FragmentRefreshTests.Due);
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeReplace = async (_, _) =>
        {
            stalled.TrySetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        Task<IdentityGateway.Answer> orders = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar, Orders), Ct);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        gateway.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(500);
        gateway.Idp.RevokeAllForClient(Shell.OAuth.ClientId);
        Task<IdentityGateway.Answer> shell = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar), Ct);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Task<IdentityGateway.Answer> logout = Task.Run(() => gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST"), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        Stopwatch sinceRelease = Stopwatch.StartNew();
        hold.Release();

        IdentityGateway.Answer ended = await shell;
        IdentityGateway.Answer loggedOut = await logout;
        sinceRelease.Stop();
        resume.TrySetResult();
        await orders;

        Assert.Equal((401, "session_ended"), (ended.Status, ended.Body));
        Assert.Equal(302, loggedOut.Status);
        Assert.True(sinceRelease.Elapsed < TimeSpan.FromSeconds(2), $"logout returned {sinceRelease.Elapsed} after the release");
        Assert.Null(await store.Inner.GetAsync(key, Ct));
    }

    [Fact]
    public async Task The_revocations_run_in_parallel_under_one_provider_deadline()
    {
        // #995 -> "Logout", step 3: every set revoked in parallel under one ProviderDeadline (2 s here). The
        // provider holds each revocation for 5 s, so all three end deadline_exceeded, the record is still
        // removed, and the logout takes about one deadline, not three. Mutation: revoke the fragments' sets
        // one after another; logout takes about 6 s, and this fails (1 test red).
        CapturedLog logs = new();
        AccessTokenSourceOptions small = new() { ProviderDeadline = TimeSpan.FromSeconds(2) };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider).AddSingleton(small));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway);
        gateway.Idp.Faults.RevokeDelay = TimeSpan.FromSeconds(5);
        Stopwatch elapsed = Stopwatch.StartNew();

        IdentityGateway.Answer logout = await gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST");

        elapsed.Stop();
        Assert.Equal(302, logout.Status);
        Assert.Equal(3, LogoutLines(logs).Count(l => l.EndsWith(": deadline_exceeded.", StringComparison.Ordinal)));
        Assert.Null(await gateway.Sessions.GetAsync(key, Ct));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4), $"logout took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task N16_no_token_reaches_any_line_of_a_fragment_refresh_or_of_the_per_set_logout()
    {
        // N-16's canary search over every new line: a sign-in with two fragments, a fragment's refresh, a
        // fragment's refusal, and a logout with its per-set lines. No token the provider issued, and
        // nothing shaped like a JWT, reaches the log; nor does the session key. Mutation: log the presented
        // refresh token on the fragment's write line; the search finds it, and this fails (1 test red).
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], s => s.AddSingleton(logs.Provider));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: FragmentRefreshTests.AllDue);

        Assert.Equal(200, (await FragmentRefreshTests.TokenAsync(gateway, jar, Orders)).Status);
        gateway.Idp.RevokeAllForClient(Billing.OAuth.ClientId);
        Assert.Equal(401, (await FragmentRefreshTests.TokenAsync(gateway, jar, Billing)).Status);
        Assert.Equal(302, (await gateway.SendAsync(Shell, IdentityGateway.LogoutPath, jar, "POST")).Status);

        string[] ours = [.. logs.Lines
            .Where(l => l.Category == typeof(AccessTokenSource).FullName || l.Category == typeof(SessionEnd).FullName)
            .Select(l => l.Text)];
        Assert.Contains(ours, l => l.Contains("fragment orders", StringComparison.Ordinal) && l.StartsWith("Refresh write", StringComparison.Ordinal));
        Assert.Contains(ours, l => l.Contains("fragment billing", StringComparison.Ordinal) && l.Contains("has ended", StringComparison.Ordinal));
        Assert.Contains(ours, l => l.Contains("of fragment orders:", StringComparison.Ordinal));
        string all = string.Join("\n", logs.Lines.Select(l => l.Text));
        TokenSearch.AssertNoTokens(gateway.Idp, all);
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
    }
}
