using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-08, whole: "Unknown host, right host with the wrong port, spoofed <c>X-Forwarded-Host</c> ->
/// 404 for the first two. The spoofed header changes nothing." (docs/plan/04-tests.md.)
///
/// <para><b>Why the raw socket.</b> The authority <i>is</i> the input under test, so it has to reach
/// the process as written. An HTTP client library derives <c>Host</c> from the URL it was given, and
/// in an agent sandbox the environment's proxy answers on that header itself, so a spoofed or
/// mismatched authority never arrives and a 400 comes back with nothing listening (AGENTS.md).
/// <see cref="RawHttp"/> writes the request line and <c>Host</c> itself over a bare socket, which is
/// the only way this row can be driven.</para>
///
/// <para><b>Stage 4 answers before routing</b> (<c>contracts/pipeline.md</c>), which is what keeps an
/// authentication callback and a proxy route out of reach on a host the registry does not name
/// (evidence S-A3). That is asserted here as a client can see it: an <c>/api</c> path on an unknown
/// host comes back as the host refusal, not as the proxy's own <c>unknown_api</c>, and no upstream
/// records the request.</para>
/// </summary>
public sealed class HostRefusalTests(HostRefusalTests.Cell cell) : IClassFixture<HostRefusalTests.Cell>
{
    /// <summary>One harness for this class: every row here reads the registry the harness started with.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>The host the registry names for app 1, read from the harness rather than written down.</summary>
    private string KnownHost => Harness.App("app1").Host;

    [Fact]
    public async Task The_gateway_answers_on_this_socket_so_a_404_is_a_refusal_and_not_a_dead_port()
    {
        // The control for every assertion below. Stage 3 answers /healthz "on any host" before host
        // resolution runs (contracts/pipeline.md), so a 200 here on a host the registry does not name
        // proves the socket is live and the 404s that follow were decided by stage 4.
        RawResponse health = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/healthz", "nobody.spa-poc.test:8443", ct: Suite.Ct);

        Assert.Equal(200, health.StatusCode);
    }

    [Fact]
    public async Task An_unknown_host_is_404_under_host_unknown()
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", "nobody.spa-poc.test:8443", ct: Suite.Ct);

        await AssertHostRefusedAsync(response, before);
    }

    [Fact]
    public async Task The_right_host_with_the_wrong_port_is_404_under_host_unknown()
    {
        // The registry's host carries a port (fixtures/apps/app1.json, "app1.spa-poc.test:8443"), and
        // stage 4 compares the whole authority ordinally. The name alone is therefore not the host:
        // a listener reachable on another port must not serve the app.
        string name = KnownHost.Split(':')[0];
        string wrongPort = name + ":9443";
        Assert.NotEqual(KnownHost, wrongPort);

        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", wrongPort, ct: Suite.Ct);

        await AssertHostRefusedAsync(response, before);

        // And the name on its own, with no port at all, is a different authority again.
        before = await Observed.SettledCountAsync(Harness, Suite.Ct);
        RawResponse bare = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", "/", name, ct: Suite.Ct);
        await AssertHostRefusedAsync(bare, before);
    }

    [Fact]
    public async Task A_spoofed_x_forwarded_host_cannot_select_another_app()
    {
        // Stage 1 never reads X-Forwarded-Host, and the harness's stand-in load balancer passes one
        // through untouched exactly as the ALB does, so this is the request the cell would see.
        // The observable is whose policy comes back: app 2's entry adds a source app 1's does not
        // (fixtures/apps/app2.json), read from the fixture rather than written down here.
        string app2Addition = FixtureFiles.AppDefinition("app2")["csp"]!["img-src"]!.AsArray().Single()!.GetValue<string>();

        RawResponse served = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", KnownHost,
            [("X-Forwarded-Host", Harness.App("app2").Host)], Suite.Ct);

        Assert.Equal(200, served.StatusCode);

        string policy = Assert.Single(served.Values("Content-Security-Policy"));
        Assert.DoesNotContain(app2Addition, policy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self';", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spoofed_x_forwarded_host_cannot_rescue_an_unknown_host()
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", "nobody.spa-poc.test:8443",
            [("X-Forwarded-Host", KnownHost)], Suite.Ct);

        await AssertHostRefusedAsync(response, before);
    }

    [Fact]
    public async Task On_an_unknown_host_no_route_and_no_upstream_is_reached()
    {
        int upstreamsBefore = Harness.Echo.Requests.Count;
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        // An API path the app does list. If routing ran, this would reach the echo upstream; if
        // routing ran and the name were unknown, the proxy's catch-all would answer unknown_api.
        // Stage 4 is before both, so the answer is the host refusal and nothing was forwarded.
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/api/echo/anything", "nobody.spa-poc.test:8443", ct: Suite.Ct);

        await AssertHostRefusedAsync(response, before);
        Assert.DoesNotContain("unknown_api", RawBody.Text(response), StringComparison.Ordinal);
        Assert.Equal(upstreamsBefore, Harness.Echo.Requests.Count);
    }

    private async Task AssertHostRefusedAsync(RawResponse response, int before)
    {
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains("\"code\":\"not_found\"", RawBody.Text(response), StringComparison.Ordinal);

        Assert.Equal(RuleIds.HostUnknown, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }
}
