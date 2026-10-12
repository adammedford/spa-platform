using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The app feature double. Host resolution sets this per request (task 2.3) and everything after it
/// reads it, so almost every component test needs one. The behaviour worth pinning is that the pair
/// it hands out is coherent: an app that is not in its own snapshot would be a state the real
/// pipeline can never produce, and a test built on it would prove nothing.
/// </summary>
public sealed class SettableAppFeatureTests
{
    [Fact]
    public void The_feature_returns_the_app_and_snapshot_it_was_given()
    {
        AppDefinition app = TestApps.Entry("app1");
        RegistrySnapshot snapshot = InMemoryRegistry.SnapshotOf(7, "\"etag\"", [app]);

        SettableAppFeature feature = new(app, snapshot);

        Assert.Same(app, feature.App);
        Assert.Same(snapshot, feature.Snapshot);
    }

    [Fact]
    public void A_feature_built_for_one_app_resolves_that_app_in_its_own_snapshot()
    {
        AppDefinition app = TestApps.Entry("app1");

        SettableAppFeature feature = SettableAppFeature.For(app);

        Assert.Same(app, feature.Snapshot.ByHost[app.Host]);
        Assert.Same(app, feature.Snapshot.ByAppId["app1"]);
    }

    [Fact]
    public void Setting_the_app_moves_the_feature_to_it()
    {
        // The test browser in task 6.1 drives two hosts through one pipeline, so the feature has to
        // be settable between requests.
        AppDefinition app2 = TestApps.Entry("app2");
        SettableAppFeature feature = SettableAppFeature.For(TestApps.Entry("app1"));

        feature.App = app2;

        Assert.Same(app2, feature.App);
    }

    [Fact]
    public void Setting_the_snapshot_moves_the_feature_to_it()
    {
        SettableAppFeature feature = SettableAppFeature.For(TestApps.Entry("app1"));
        RegistrySnapshot next = InMemoryRegistry.SnapshotOf(8, "\"next\"", [TestApps.Entry("app1")]);

        feature.Snapshot = next;

        Assert.Same(next, feature.Snapshot);
    }
}
