using System.Text.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// Stage 4, <c>UsePlatformHostResolution</c> (contracts/pipeline.md; GLOSSARY.md, Host): the request
/// authority must equal a registry <c>host</c> exactly, the app and the snapshot are pinned for the
/// request as <see cref="IAppFeature"/>, and any other host gets 404 before routing runs.
/// </summary>
public sealed class HostResolutionTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1");
    private static readonly AppDefinition App2 = TestApps.Entry("app2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HostingHost> StartAsync() => HostingHost.StartAsync(Snapshots.With(3, App1, App2), Ct);

    [Fact]
    public async Task A_known_host_passes_with_its_app_and_the_snapshot_pinned()
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse one = await gateway.GetAsync("app1.spa-poc.test", "/", Ct);
        Assert.Equal(200, one.Status);
        Assert.Equal(("app1", "app1.spa-poc.test", 3L), gateway.LastSeen);

        RawResponse two = await gateway.GetAsync("app2.spa-poc.test", "/orders/42", Ct);
        Assert.Equal(200, two.Status);
        Assert.Equal(("app2", "app2.spa-poc.test", 3L), gateway.LastSeen);
    }

    /// <summary>
    /// S-A3 (docs/plan/07-evidence.md): an unknown host gets 404 on every path, the callback path
    /// included, so no scheme handler ever sees a host the registry does not name. The paths are the
    /// ones each later stage owns (contracts/http-contract.md).
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/signin-oidc")]
    [InlineData("/signout-callback-oidc")]
    [InlineData("/bff/login")]
    [InlineData("/bff/session")]
    [InlineData("/api/orders/42")]
    public async Task S_A3_an_unknown_host_gets_404_not_found_on_every_path(string path)
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse response = await gateway.GetAsync("nope.example", path, Ct);

        AssertNotFound(response);
        Assert.Equal(0, gateway.Reached);
    }

    /// <summary>
    /// N-08, first two rows (docs/plan/04-tests.md): an unknown host, and a known host with the wrong
    /// port. The host is the whole authority, port included (GLOSSARY.md, Host), so a port the entry
    /// does not name is a different host, and so is leaving off a port it does name.
    /// </summary>
    [Theory]
    [InlineData("evil.example")]
    [InlineData("app9.spa-poc.test")]
    [InlineData("app1.spa-poc.test:8443")]
    [InlineData("app1.spa-poc.test:80")]
    [InlineData("app3.spa-poc.test")]
    [InlineData("app3.spa-poc.test:8444")]
    [InlineData("spa-poc.test")]
    [InlineData("127.0.0.1")]
    public async Task N_08_an_unknown_host_or_the_wrong_port_gets_404(string host)
    {
        AppDefinition app3 = TestApps.Entry("app3", host: "app3.spa-poc.test:8443");
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(3, App1, App2, app3), Ct);

        AssertNotFound(await gateway.GetAsync(host, "/", Ct));
        Assert.Equal(0, gateway.Reached);

        // The control: the same gateway resolves the authority the entry does name.
        RawResponse named = await gateway.GetAsync("app3.spa-poc.test:8443", "/", Ct);
        Assert.Equal(200, named.Status);
        Assert.Equal(("app3", "app3.spa-poc.test:8443", 3L), gateway.LastSeen);
    }

    /// <summary>
    /// N-08, third row: a spoofed <c>X-Forwarded-Host</c> changes nothing. Stage 4 reads the
    /// authority the request carries and never the forwarded header (contracts/pipeline.md, stage 1:
    /// <c>X-Forwarded-Host</c> is never read). Both directions: a known name in the header does not
    /// admit an unknown host, and an unknown one does not move a known host to another app or refuse
    /// it. Stage 1 is workstream 4's; workstream 6 proves the two together in the composed gateway.
    /// </summary>
    [Fact]
    public async Task N_08_a_spoofed_X_Forwarded_Host_changes_nothing()
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse admitted = await gateway.GetAsync(
            "nope.example", "/", Ct, "X-Forwarded-Host: app1.spa-poc.test\r\n");
        AssertNotFound(admitted);
        Assert.Equal(0, gateway.Reached);

        RawResponse moved = await gateway.GetAsync(
            "app1.spa-poc.test", "/", Ct, "X-Forwarded-Host: app2.spa-poc.test\r\n");
        Assert.Equal(200, moved.Status);
        Assert.Equal(("app1", "app1.spa-poc.test", 3L), gateway.LastSeen);

        RawResponse refused = await gateway.GetAsync(
            "app2.spa-poc.test", "/", Ct, "X-Forwarded-Host: evil.example\r\n");
        Assert.Equal(200, refused.Status);
        Assert.Equal(("app2", "app2.spa-poc.test", 3L), gateway.LastSeen);
    }

    /// <summary>
    /// Exact means ordinal (GLOSSARY.md, Host: the authority the browser sends, lowercase). Proven on
    /// a snapshot whose index ignores case, <c>InMemoryRegistry.SnapshotOf</c>'s, so the test fails if
    /// stage 4 leaves exactness to whichever comparer the snapshot was built with.
    /// </summary>
    [Theory]
    [InlineData("APP1.spa-poc.test")]
    [InlineData("app1.SPA-POC.test")]
    public async Task Matching_is_exact_even_when_the_snapshot_index_ignores_case(string host)
    {
        RegistrySnapshot lenient = InMemoryRegistry.SnapshotOf(3, "\"etag-3\"", [App1]);
        Assert.True(lenient.ByHost.ContainsKey(host), "the index under test must ignore case, or this proves nothing.");
        await using HostingHost gateway = await HostingHost.StartAsync(lenient, Ct);

        AssertNotFound(await gateway.GetAsync(host, "/", Ct));
        Assert.Equal(0, gateway.Reached);
    }

    /// <summary>
    /// The app and the snapshot are pinned for the request (IAppFeature): a swap that lands while the
    /// request is in a later stage does not change what that stage sees, and the next request sees
    /// the new snapshot.
    /// </summary>
    [Fact]
    public async Task A_swap_mid_request_leaves_the_pinned_app_and_snapshot_alone()
    {
        await using HostingHost gateway = await StartAsync();
        AppDefinition moved = TestApps.Entry("app1", release: "2026-10-13-def5678");
        gateway.DuringRequest = () => gateway.Registry.Swap(Snapshots.With(4, moved, App2));

        await gateway.GetAsync("app1.spa-poc.test", "/", Ct);
        Assert.Equal(("app1", "app1.spa-poc.test", 3L), gateway.LastSeen);

        gateway.DuringRequest = null;
        await gateway.GetAsync("app1.spa-poc.test", "/", Ct);
        Assert.Equal(("app1", "app1.spa-poc.test", 4L), gateway.LastSeen);
    }

    /// <summary>
    /// Before the first snapshot every host is unknown, including one a later document will name: the
    /// gateway answers 404 rather than guessing, and resolves the host once the snapshot is live.
    /// </summary>
    [Fact]
    public async Task Before_a_snapshot_every_host_is_unknown()
    {
        await using HostingHost gateway = await HostingHost.StartAsync(Snapshots.None, Ct);

        AssertNotFound(await gateway.GetAsync("app1.spa-poc.test", "/", Ct));

        gateway.Registry.Swap(Snapshots.With(1, App1));
        Assert.Equal(200, (await gateway.GetAsync("app1.spa-poc.test", "/", Ct)).Status);
        Assert.Equal(1, gateway.Reached);
    }

    /// <summary>The 404 for an unknown host is the contract's error body, code <c>not_found</c>.</summary>
    private static void AssertNotFound(RawResponse response)
    {
        Assert.Equal(404, response.Status);
        Assert.Equal("application/problem+json", response.Headers["content-type"].Split(';')[0]);
        using JsonDocument body = JsonDocument.Parse(response.Body);
        Assert.Equal("not_found", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
    }
}
