using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// The admission rule at shutdown (change request P2-3, second amendment, 2026-10-29, decision 3; #560):
/// once <c>ApplicationStopping</c> is observed at admission, after the awaited acquire and re-read and
/// before the provider call, no provider refresh starts on either refresh path. The refusal releases any
/// acquired lease and answers <c>ProviderUnavailable</c> with the session kept; a refresh admitted
/// before the stop keeps its own provider deadline and writes with no shutdown cancellation.
///
/// The signal is production's: <see cref="IdentityGateway.StopAsync"/> stops the host
/// <c>AddPlatformIdentity</c> was called on, and the token source reads
/// <c>IHostApplicationLifetime.ApplicationStopping</c> from that host's container.
/// </summary>
public sealed class StoppingHostRefreshTests
{
    private static readonly AppDefinition App1 = RefreshLeaseTests.App1;

    [Theory]
    [InlineData("acquire")]
    [InlineData("reread")]
    public async Task A_stop_observed_while_the_acquire_or_the_reread_is_pending_refuses_the_refresh_and_releases_the_lease(string pending)
    {
        // Decision 3 on the lease path: the host stops while the holder's acquire, or its re-read under
        // the lease, is in flight. The refresh is refused at admission: no grant, the lease given back,
        // 503 idp_unavailable, the record as it was, and no second acquisition, since a holder refused
        // at the stop does not wait as a loser for a refresh no task of this host will make. Mutation:
        // read the signal only before the acquire; the stop lands after that read, the provider is
        // called, and both rows fail.
        CapturedLog logs = new();
        ScriptedReadStore store = new(new InMemorySessionStore());
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        IdentityGateway? gateway = null;
        leases.AfterAcquire = pending == "acquire"
            ? _ => gateway!.StopAsync()
            : _ =>
            {
                store.OnNextGet = () => gateway!.StopAsync();
                return Task.CompletedTask;
            };
        await using IdentityGateway started = gateway = await IdentityGateway.StartAsync([App1], leases: leases,
            sessions: new RecordingSessionStore(store), services: s => s.AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(gateway);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.True(gateway.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Equal((503, "idp_unavailable"), (answer.Status, answer.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Equal(Assert.Single(leases.Acquired), Assert.Single(leases.Released));
        Assert.Equal(atLogin, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Empty(answer.Cookies);
        string line = Assert.Single(RefreshLeaseTests.SourceLines(logs), l => l.Contains("stopping", StringComparison.Ordinal));
        Assert.Contains(SessionRef.Of(key), line, StringComparison.Ordinal);
        AssertNoSecretOnAnyLine(logs, key, leases);
    }

    [Fact]
    public async Task A_refresh_admitted_just_before_the_stop_is_carried_out_and_written_despite_the_signal()
    {
        // Decision 3's other half and decision 2's promise: the host stops while the provider holds a
        // refresh admitted before the stop. The refresh keeps its own deadline, the grant is made, the
        // rotated token is written, and the call answers it. Mutation: link the provider deadline, or the
        // write, to ApplicationStopping; the held refresh is cancelled at the stop, and this fails.
        HeldClock hold = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], leases: leases, idpClock: hold);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(gateway);
        (string key, _) = await gateway.Sessions.SingleAsync();
        string loginRefresh = await RefreshLeaseTests.StoredTokenAsync(gateway.Sessions, key, "refresh_token");
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        Task<IdentityGateway.Answer> call = Task.Run(
            () => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar), TestContext.Current.CancellationToken);
        await hold.Holding.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        gateway.Idp.Faults.TokenDelay = TimeSpan.Zero;
        await gateway.StopAsync();
        Assert.True(gateway.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(call.IsCompleted);
        hold.Release();
        IdentityGateway.Answer answer = await call;

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Equal(grant.RefreshTokenIssued, await RefreshLeaseTests.StoredTokenAsync(gateway.Sessions, key, "refresh_token"));
        Assert.NotEqual(loginRefresh, grant.RefreshTokenIssued);
        Assert.Equal(Assert.Single(leases.Acquired), Assert.Single(leases.Released));
    }

    [Fact]
    public async Task With_no_lease_table_a_stop_observed_before_the_provider_call_refuses_the_refresh_and_keeps_the_session()
    {
        // Decision 3 on the path with no IRefreshLeases registered, one replica where the session gate is
        // the whole refresh gate: the host has stopped when a due token's refresh reaches admission, after
        // the gated read. No provider call, 503 idp_unavailable, the record and the cookie as they were,
        // and the next call on a host still up would refresh. Mutation: gate the check on a lease being
        // held; this path has none, the provider is called, and this fails.
        CapturedLog logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], services: s => s.AddSingleton(logs.Provider));
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(gateway);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();
        await gateway.StopAsync();

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (answer.Status, answer.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Equal(atLogin, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Empty(answer.Cookies);
        string line = Assert.Single(RefreshLeaseTests.SourceLines(logs), l => l.Contains("stopping", StringComparison.Ordinal));
        Assert.Contains(SessionRef.Of(key), line, StringComparison.Ordinal);
        AssertNoSecretOnAnyLine(logs, key);
    }

    /// <summary>
    /// N-16 on the new paths: neither the session key nor any lease token is on any line the host wrote.
    /// </summary>
    private static void AssertNoSecretOnAnyLine(CapturedLog logs, string key, ScriptedLeases? leases = null)
    {
        Assert.NotEmpty(logs.Lines);
        Assert.All(logs.Lines, line =>
        {
            Assert.DoesNotContain(key, line.Text, StringComparison.Ordinal);
            foreach (RefreshLease lease in leases?.Acquired ?? [])
            {
                Assert.DoesNotContain(lease.Token, line.Text, StringComparison.Ordinal);
            }
        });
    }
}
