using System.Net;
using System.Net.Http.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// The second seam: the routes as YARP actually honours them, over a socket at each end. This is
/// where test N-10 is proven, and where the half of it that is task 4.1's is decided: an API name
/// the app does not list is refused, and a path built to escape the prefix reaches no upstream but
/// the app's own.
/// </summary>
public sealed class ProxyRouteTests
{
    [Fact]
    public async Task A_listed_api_reaches_its_upstream_with_the_prefix_removed()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items?page=2"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EchoedRequest received = Assert.Single(orders.Requests);
        Assert.Equal("/items", received.Path);
        Assert.Equal("?page=2", received.Query);
    }

    [Fact]
    public async Task An_api_name_the_app_does_not_list_is_refused_and_no_upstream_sees_it()
    {
        // The first half of N-10.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unknown_api", await CodeOf(response));
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task An_api_another_app_lists_is_not_reachable_from_this_host()
    {
        // Host and path together select the route, so app 1 asking for app 2's API name is simply an
        // unlisted name on app 1.
        EchoUpstream reports = new();
        await using EchoUpstreamListener listener = await reports.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1"),
            TestApps.Entry("app2", apis: [TestApps.Api("reports", listener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown_api", await CodeOf(response));
        Assert.Empty(reports.Requests);
    }

    [Theory]
    [InlineData("/api/orders/%2e%2e%2fsecret")]
    [InlineData("/api/orders/%2e%2e%5csecret")]
    [InlineData("/api/orders/..;/secret")]
    [InlineData("/api/orders//../secret")]
    public async Task A_path_built_to_escape_the_prefix_reaches_no_other_upstream(string path)
    {
        // The second half of N-10, as far as this task carries it. Finding T-C10: YARP forwards these
        // to the app's own upstream, and none of them reached another app's. The request gate in task
        // 4.3 is what refuses them outright, with 400 path.invalid.
        EchoUpstream orders = new();
        EchoUpstream others = new();
        await using EchoUpstreamListener ordersListener = await orders.ListenAsync(Ct);
        await using EchoUpstreamListener othersListener = await others.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [TestApps.Api("orders", ordersListener.BaseAddress.AbsoluteUri)]),
            TestApps.Entry("app2", apis: [TestApps.Api("secret", othersListener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new();

        await client.SendAsync(gateway.Request(HttpMethod.Get, "app1.spa-poc.test", path), Ct);

        Assert.Empty(others.Requests);
    }

    [Fact]
    public async Task A_registry_swap_moves_the_live_route()
    {
        // "Replaced atomically when the registry swaps", end to end: the route an app gains after a
        // swap forwards, with no restart.
        EchoUpstream reports = new();
        await using EchoUpstreamListener listener = await reports.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, TestApps.Entry("app1"));
        using HttpClient client = new();

        HttpResponseMessage before = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        gateway.Registry.Swap(InMemoryRegistry.SnapshotOf(2, "\"next\"", [
            TestApps.Entry("app1", apis: [TestApps.Api("reports", listener.BaseAddress.AbsoluteUri)]),
        ]));

        HttpResponseMessage after = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("/items", Assert.Single(reports.Requests).Path);
    }

    [Fact]
    public async Task With_no_apps_at_all_an_api_call_is_still_refused_with_unknown_api()
    {
        // The skeleton, and G1a's exit test runs it: no registry, so no route, and /api must still
        // answer from the catch-all rather than fall through to an asset.
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct);
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown_api", await CodeOf(response));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())?.Code;

    private sealed record Problem(int Status, string Code);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
