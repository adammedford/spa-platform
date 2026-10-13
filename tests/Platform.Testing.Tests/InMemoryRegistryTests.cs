using System.Collections.Immutable;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The registry double. The ordering here is what task 3.6 relies on: "swap before evict". A
/// subscriber that ran before the swap would rebuild its cache from the snapshot that is going away.
/// </summary>
public sealed class InMemoryRegistryTests
{
    [Fact]
    public void A_new_registry_serves_the_snapshot_it_was_given()
    {
        RegistrySnapshot snapshot = Snapshot(revision: 1, Entry("app1"));
        InMemoryRegistry registry = new(snapshot);

        Assert.Same(snapshot, registry.Current);
    }

    [Fact]
    public void A_swap_replaces_the_current_snapshot()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        RegistrySnapshot next = Snapshot(revision: 2, Entry("app1"), Entry("app2"));

        registry.Swap(next);

        Assert.Same(next, registry.Current);
    }

    [Fact]
    public void A_subscriber_sees_the_old_snapshot_then_the_new_one()
    {
        RegistrySnapshot first = Snapshot(revision: 1, Entry("app1"));
        RegistrySnapshot second = Snapshot(revision: 2, Entry("app2"));
        InMemoryRegistry registry = new(first);
        List<(RegistrySnapshot Old, RegistrySnapshot New)> seen = [];
        registry.OnSwapped((old, @new) => seen.Add((old, @new)));

        registry.Swap(second);

        (RegistrySnapshot Old, RegistrySnapshot New) raised = Assert.Single(seen);
        Assert.Same(first, raised.Old);
        Assert.Same(second, raised.New);
    }

    [Fact]
    public void Current_is_already_the_new_snapshot_while_a_subscriber_runs()
    {
        // "Raised after the swap, never before." Task 3.6 swaps before it evicts.
        RegistrySnapshot second = Snapshot(revision: 2, Entry("app2"));
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        RegistrySnapshot? currentDuringCallback = null;
        registry.OnSwapped((_, _) => currentDuringCallback = registry.Current);

        registry.Swap(second);

        Assert.Same(second, currentDuringCallback);
    }

    [Fact]
    public void Constructing_a_registry_notifies_nobody()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        int calls = 0;

        registry.OnSwapped((_, _) => calls++);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Every_subscriber_is_called()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        int first = 0;
        int second = 0;
        registry.OnSwapped((_, _) => first++);
        registry.OnSwapped((_, _) => second++);

        registry.Swap(Snapshot(revision: 2, Entry("app1")));

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Disposing_a_subscription_stops_its_callbacks()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        int calls = 0;
        IDisposable subscription = registry.OnSwapped((_, _) => calls++);

        subscription.Dispose();
        registry.Swap(Snapshot(revision: 2, Entry("app1")));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Disposing_one_subscription_leaves_the_others()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        int kept = 0;
        IDisposable dropped = registry.OnSwapped((_, _) => Assert.Fail("this subscription was disposed"));
        registry.OnSwapped((_, _) => kept++);

        dropped.Dispose();
        registry.Swap(Snapshot(revision: 2, Entry("app1")));

        Assert.Equal(1, kept);
    }

    [Fact]
    public void Disposing_a_subscription_twice_is_not_an_error()
    {
        InMemoryRegistry registry = new(Snapshot(revision: 1, Entry("app1")));
        IDisposable subscription = registry.OnSwapped((_, _) => { });

        subscription.Dispose();
        subscription.Dispose();
    }

    [Fact]
    public void A_snapshot_is_indexed_by_host_and_by_appId()
    {
        // Host resolution looks up by authority (task 2.3); spactl and the purge path look up by
        // appId.
        AppDefinition app1 = Entry("app1");
        RegistrySnapshot snapshot = Snapshot(revision: 1, app1, Entry("app2"));

        Assert.Same(app1, snapshot.ByHost["app1.spa-poc.test"]);
        Assert.Same(app1, snapshot.ByAppId["app1"]);
        Assert.Equal(2, snapshot.ByHost.Count);
    }

    private static RegistrySnapshot Snapshot(long revision, params AppDefinition[] apps) =>
        InMemoryRegistry.SnapshotOf(revision, "\"etag-" + revision + "\"", apps);

    private static AppDefinition Entry(string appId) =>
        new(
            AppId: appId,
            Host: appId + ".spa-poc.test",
            Release: "2026-10-12-abc1234",
            OAuth: new OAuthDefinition("primary", "client-" + appId, "ssm:/secret:1", ["openid"], Resource: null),
            Apis: [],
            Csp: ImmutableDictionary<string, ImmutableArray<string>>.Empty,
            SessionLifetime: TimeSpan.FromHours(8),
            Fingerprint: "sha256:" + appId);
}
