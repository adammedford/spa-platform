using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// The two-host composed harness of plan row P2-5 (#345; #332, T7): two gateway hosts in one test
/// process over one bucket, one set of client secrets, one session store, one lease table and the
/// real <c>AddPlatformHosting</c> key ring over one Parameter Store stub, each naming its own
/// deterministic instance on <c>/readyz</c>.
/// </summary>
public sealed class TwoHostHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task P2_5_two_hosts_start_over_the_shared_doubles_and_the_real_ring_and_each_readyz_names_its_own_instance()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];

        var readyA = await a.ReadyzAsync(Ct);
        var readyB = await b.ReadyzAsync(Ct);

        // Mutation that turns it red: dropping the harness's GatewayInstance registration (each host then
        // names a generated id), or registering the Parameter Store stub as a plain test service in place
        // of StubUnderTheRing (RemoveAll then AddSingleton): the container then hands out the bare stub,
        // past the shield AddPlatformHosting puts over the client it finds.
        Assert.Equal((HttpStatusCode.OK, 1L, "host-a"), readyA);
        Assert.Equal((HttpStatusCode.OK, 1L, "host-b"), readyB);
        Assert.NotEqual(a.Address, b.Address);

        // One ring: the first host minted the cell's first key into the one stub, the second read it and
        // minted none.
        Assert.Same(a.ParameterStore, b.ParameterStore);
        Assert.Single(a.ParameterStore!.Puts);
        Assert.StartsWith("/spa-platform/harness/data-protection/", a.ParameterStore.Puts[0], StringComparison.Ordinal);

        // Under the ring, where a client registered before Program.cs would be: AddPlatformHosting's shield
        // (#216) is the client the container hands out, not the stub, and the writes above still reached
        // the stub through it. A stub registered as a plain test service would be handed out bare.
        foreach (GatewayHost host in harness.Hosts)
        {
            object? resolved = host.Services.GetService(typeof(Amazon.SimpleSystemsManagement.IAmazonSimpleSystemsManagement));
            Assert.NotNull(resolved);
            Assert.NotSame(host.ParameterStore, resolved);
        }

        // One session store and one lease table, the harness's, in both hosts.
        Assert.Same(harness.SessionStore, a.Services.GetService(typeof(ISessionStore)));
        Assert.Same(harness.SessionStore, b.Services.GetService(typeof(ISessionStore)));
        Assert.Same(harness.RefreshLeases, a.Services.GetService(typeof(IRefreshLeases)));
        Assert.Same(harness.RefreshLeases, b.Services.GetService(typeof(IRefreshLeases)));
    }

    [Fact]
    public async Task P2_5_the_stand_in_load_balancer_alternates_between_the_hosts()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        List<string?> answered = [];
        for (int i = 0; i < 4; i++)
        {
            BrowserResponse ready = await browser.NavigateAsync("https://cell.spa-poc.test:8443/readyz", ct: Ct);
            answered.Add(Readyz.Instance(ready));
        }

        // Mutation that turns it red: a balancer that always picks the first host (CreateLoadBalancer
        // returning hosts[0]) answers host-a four times.
        Assert.Equal(["host-a", "host-b", "host-a", "host-b"], answered);
    }
}
