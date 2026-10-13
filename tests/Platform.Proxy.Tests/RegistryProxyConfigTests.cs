using Platform.Abstractions;
using Platform.Testing;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.1 at its first seam: the routes and clusters a snapshot produces. ADR-0006 is what this
/// file holds to. No request input reaches destination selection, so the only thing that can steer
/// the proxy is the snapshot, and this is where the snapshot is read.
/// </summary>
public sealed class RegistryProxyConfigTests
{
    [Fact]
    public void One_route_and_one_cluster_per_app_and_api()
    {
        AppDefinition app1 = TestApps.Entry("app1", apis: [TestApps.Api("orders"), TestApps.Api("reports")]);
        AppDefinition app2 = TestApps.Entry("app2", apis: [TestApps.Api("reports")]);
        IProxyConfig config = RegistryProxyConfig.From(SnapshotOf(app1, app2));

        Assert.Equal(
            ["app1/orders", "app1/reports", "app2/reports"],
            config.Routes.Select(route => route.RouteId).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["app1/orders", "app1/reports", "app2/reports"],
            config.Clusters.Select(cluster => cluster.ClusterId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_route_matches_the_app_host_and_its_own_api_prefix()
    {
        AppDefinition app = TestApps.Entry("app1", apis: [TestApps.Api("orders")]);

        RouteConfig route = Assert.Single(RegistryProxyConfig.From(SnapshotOf(app)).Routes);

        // Host and path together. The host is why app 2 cannot reach app 1's upstream by asking for
        // an API name app 1 happens to list.
        Assert.Equal(["app1.spa-poc.test"], route.Match.Hosts);
        Assert.Equal("/api/orders/{**rest}", route.Match.Path);
    }

    [Fact]
    public void A_route_removes_the_api_prefix_before_forwarding()
    {
        AppDefinition app = TestApps.Entry("app1", apis: [TestApps.Api("orders")]);

        RouteConfig route = Assert.Single(RegistryProxyConfig.From(SnapshotOf(app)).Routes);

        // The upstream contract: "Path with /api/{name} removed."
        IReadOnlyDictionary<string, string> transform = Assert.Single(route.Transforms!);
        Assert.Equal("/api/orders", transform["PathRemovePrefix"]);
    }

    [Fact]
    public void A_cluster_has_the_one_upstream_the_entry_names()
    {
        AppDefinition app = TestApps.Entry(
            "app1", apis: [TestApps.Api("orders", upstream: "http://127.0.0.1:5080")]);

        ClusterConfig cluster = Assert.Single(RegistryProxyConfig.From(SnapshotOf(app)).Clusters);

        // One destination, from the snapshot. ADR-0006 rejected deriving it from the request.
        DestinationConfig destination = Assert.Single(cluster.Destinations!).Value;
        Assert.Equal("http://127.0.0.1:5080/", destination.Address);
    }

    [Fact]
    public void A_cluster_carries_the_entry_timeout_as_the_activity_timeout()
    {
        AppDefinition app = TestApps.Entry("app1", apis: [TestApps.Api("orders", timeoutSeconds: 7)]);

        ClusterConfig cluster = Assert.Single(RegistryProxyConfig.From(SnapshotOf(app)).Clusters);

        // An idle limit, not a limit on the whole exchange: findings T-C17 and T-C18.
        Assert.Equal(TimeSpan.FromSeconds(7), cluster.HttpRequest!.ActivityTimeout);
    }

    private static RegistrySnapshot SnapshotOf(params AppDefinition[] apps) =>
        InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
