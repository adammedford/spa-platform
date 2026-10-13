using Platform.Abstractions;
using Platform.Testing;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy.Tests;

/// <summary>
/// The provider that keeps YARP's configuration equal to the live snapshot: task 4.1's "replaced
/// atomically when the registry swaps". Atomically means two things, and both are asserted here.
/// The new configuration is complete before anything points at it, and a reader holding the old one
/// keeps a whole old view rather than a half-updated one.
/// </summary>
public sealed class RegistryProxyConfigProviderTests
{
    [Fact]
    public void The_configuration_is_the_live_snapshot()
    {
        InMemoryRegistry registry = RegistryOf(TestApps.Entry("app1", apis: [TestApps.Api("orders")]));

        IProxyConfig config = new RegistryProxyConfigProvider(registry).GetConfig();

        Assert.Equal("app1/orders", Assert.Single(config.Routes).RouteId);
    }

    [Fact]
    public void A_swap_replaces_the_configuration()
    {
        InMemoryRegistry registry = RegistryOf(TestApps.Entry("app1", apis: [TestApps.Api("orders")]));
        RegistryProxyConfigProvider provider = new(registry);
        _ = provider.GetConfig();

        registry.Swap(InMemoryRegistry.SnapshotOf(
            2, "\"next\"", [TestApps.Entry("app1", apis: [TestApps.Api("reports")])]));

        Assert.Equal("app1/reports", Assert.Single(provider.GetConfig().Routes).RouteId);
    }

    [Fact]
    public void A_swap_signals_the_configuration_it_replaced()
    {
        // YARP reloads on the token, so a swap that does not signal is a swap YARP never sees.
        InMemoryRegistry registry = RegistryOf(TestApps.Entry("app1", apis: [TestApps.Api("orders")]));
        RegistryProxyConfigProvider provider = new(registry);
        IProxyConfig before = provider.GetConfig();
        Assert.False(before.ChangeToken.HasChanged);

        registry.Swap(InMemoryRegistry.SnapshotOf(2, "\"next\"", [TestApps.Entry("app1")]));

        Assert.True(before.ChangeToken.HasChanged);
    }

    [Fact]
    public void A_reader_holding_the_old_configuration_keeps_a_whole_old_view()
    {
        // The swap is a reference change, not an edit: nothing a reader already has is rewritten.
        InMemoryRegistry registry = RegistryOf(TestApps.Entry("app1", apis: [TestApps.Api("orders")]));
        RegistryProxyConfigProvider provider = new(registry);
        IProxyConfig before = provider.GetConfig();

        registry.Swap(InMemoryRegistry.SnapshotOf(2, "\"next\"", []));

        Assert.Equal("app1/orders", Assert.Single(before.Routes).RouteId);
        Assert.NotSame(before, provider.GetConfig());
    }

    [Fact]
    public void With_no_registry_registered_there_are_no_routes_at_all()
    {
        // The skeleton. AddPlatformRegistry is a stub until task 2.2, so the composed gateway has no
        // IRegistry to read and G1a's exit test still starts it. No snapshot means no route, so every
        // /api call is an unlisted name, which is what the gateway should say when it knows no app.
        IProxyConfig config = new RegistryProxyConfigProvider(registry: null).GetConfig();

        Assert.Empty(config.Routes);
        Assert.Empty(config.Clusters);
    }

    private static InMemoryRegistry RegistryOf(params AppDefinition[] apps) =>
        new(InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps));
}
