using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The bounded write and the cache behind a fragment's scheme, at the module's own interface (#994):
/// <see cref="AppSessions.Turn.MergeAsync"/> over a store that loses its replaces, with a clock the test
/// moves, and <see cref="AppOptionsCache{TOptions}"/> and <see cref="OptionsInvalidator"/> for the
/// fragment name form.
/// </summary>
public sealed class FragmentMergeTests
{
    private static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    private static readonly AppDefinition Shell = FragmentChainTests.Shell(Orders);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionRecord Record() => new(
        Shell.AppId, Shell.OAuth.ClientId, "alice", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 1, new byte[] { 1 });

    [Fact]
    public async Task No_attempt_starts_later_than_the_deadline_after_the_first_send()
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        ManualMonotonicClock clock = new();
        string key = await store.CreateAsync(Record(), Ct);
        // Each replace loses to another writer and takes six seconds against a five-second deadline.
        store.BeforeReplace = async (k, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(6));
            await store.BumpAsync(k);
        };
        AppSessions sessions = new(store);
        using AppSessions.Turn turn = await sessions.EnterAsync(Shell, key);

        MergeResult result = await turn.MergeAsync(r => r with { Ticket = new byte[] { 2 } }, 3, TimeSpan.FromSeconds(5), clock);

        Assert.Equal(new MergeResult(MergeOutcome.Exhausted, 1), result);
        Assert.Equal(1, store.Replaces);
    }

    [Fact]
    public async Task Within_the_budget_and_the_deadline_a_lost_replace_is_re_read_and_re_applied()
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        string key = await store.CreateAsync(Record(), Ct);
        store.BeforeReplace = async (k, attempt) =>
        {
            if (attempt < 3)
            {
                await store.BumpAsync(k);
            }
        };
        AppSessions sessions = new(store);
        using AppSessions.Turn turn = await sessions.EnterAsync(Shell, key);

        MergeResult result = await turn.MergeAsync(r => r with { Ticket = new byte[] { 2 } }, 3, TimeSpan.FromSeconds(5), TimeProvider.System);

        Assert.Equal(new MergeResult(MergeOutcome.Written, 3), result);
        SessionRecord written = (await store.GetAsync(key, Ct))!;
        Assert.Equal(new byte[] { 2 }, written.Ticket.ToArray());
        // Applied over the winner's version, read again after each loss: 1, bumped twice to 3, written as 4.
        Assert.Equal(4, written.Version);
    }

    [Fact]
    public async Task A_store_failure_leaves_with_the_attempts_it_had_started()
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        string key = await store.CreateAsync(Record(), Ct);
        store.BeforeReplace = async (k, attempt) =>
        {
            if (attempt == 1)
            {
                await store.BumpAsync(k);
                return;
            }

            throw ScriptedSessionStore.Unavailable("replace (PutItem)");
        };
        AppSessions sessions = new(store);
        using AppSessions.Turn turn = await sessions.EnterAsync(Shell, key);

        MergeFailedException failure = await Assert.ThrowsAsync<MergeFailedException>(() =>
            turn.MergeAsync(r => r with { Ticket = new byte[] { 2 } }, 3, TimeSpan.FromSeconds(5), TimeProvider.System));

        Assert.Equal(2, failure.Attempts);
        Assert.Equal("replace (PutItem)", failure.Failure.Operation);
    }

    [Fact]
    public void A_set_recorded_to_another_client_reads_as_absent_and_a_mark_never_overwrites_a_set()
    {
        AuthenticationProperties properties = new();
        Assert.Equal(FragmentSets.LoginRequired, FragmentSets.StateOf(properties, Orders));

        FragmentSets.WriteSet(properties, Orders,
            [new() { Name = "access_token", Value = "a" }, new() { Name = "id_token", Value = "i" }, new() { Name = "refresh_token", Value = "r" }]);
        Assert.Equal(FragmentSets.SignedIn, FragmentSets.StateOf(properties, Orders));
        Assert.Equal(["access_token", "refresh_token"], FragmentSets.TokenNames(properties, "orders").Order(StringComparer.Ordinal));
        Assert.False(FragmentSets.TryMark(properties, Orders));
        Assert.Equal(FragmentSets.SignedIn, FragmentSets.StateOf(properties, Orders));

        FragmentDefinition moved = Orders with { OAuth = Orders.OAuth with { ClientId = "client-shell-orders-2" } };
        Assert.Equal(FragmentSets.LoginRequired, FragmentSets.StateOf(properties, moved));
        Assert.True(FragmentSets.TryMark(properties, moved));
        Assert.Equal(FragmentSets.Unavailable, FragmentSets.StateOf(properties, moved));
        Assert.Empty(FragmentSets.TokenNames(properties, "orders"));

        // A mark recorded to the old client reads as absent once the client moves on again.
        Assert.Equal(FragmentSets.LoginRequired, FragmentSets.StateOf(properties, Orders));
    }

    [Fact]
    public async Task A_request_pinned_before_a_swap_builds_the_old_fragment_client_for_itself_only()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        FragmentDefinition moved = TestApps.Fragment("orders", "shell", clientId: "client-shell-orders-2",
            clientSecretRef: "ssm:/spa-platform/poc/apps/shell/fragments/orders/client-secret:2");
        AppDefinition next = FragmentChainTests.Shell(moved);
        RegistrySnapshot old = await gateway.SwapAsync(next);
        IOptionsMonitor<OpenIdConnectOptions> monitor = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        gateway.Pin(Shell, old);
        Assert.Equal(Orders.OAuth.ClientId, monitor.Get("oidc:shell:orders").ClientId);

        // The old build was not cached under the name: the live request gets the new client.
        gateway.Pin(next);
        Assert.Equal("client-shell-orders-2", monitor.Get("oidc:shell:orders").ClientId);
    }

    [Fact]
    public async Task A_swap_that_changes_the_entry_evicts_each_fragment_scheme_from_the_cache()
    {
        AppDefinition shell = FragmentChainTests.Shell(Orders, FragmentChainTests.Billing);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        gateway.Pin(shell);
        IOptionsMonitorCache<OpenIdConnectOptions> cache = gateway.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        IOptionsMonitor<OpenIdConnectOptions> monitor = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        _ = monitor.Get("oidc:shell:orders");
        _ = monitor.Get("oidc:shell:billing");

        // Billing leaves the entry: both the kept fragment's and the removed one's options go.
        await gateway.SwapAsync(FragmentChainTests.Shell(Orders));

        Assert.False(cache.TryRemove("oidc:shell:orders"));
        Assert.False(cache.TryRemove("oidc:shell:billing"));
    }
}
