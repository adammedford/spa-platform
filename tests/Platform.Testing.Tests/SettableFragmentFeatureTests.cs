using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The fragment feature double (#988 part 12). <see cref="SettableFragmentFeature.For"/> takes the
/// fragment from the entry, the only pairing host resolution can produce.
/// </summary>
public sealed class SettableFragmentFeatureTests
{
    [Fact]
    public void For_finds_the_named_fragment_of_the_entry()
    {
        // Mutation: return the first fragment whatever the name.
        FragmentDefinition orders = TestApps.Fragment("orders-ui");
        FragmentDefinition billing = TestApps.Fragment("billing-ui");
        AppDefinition app = TestApps.Entry(fragments: [orders, billing]);

        Assert.Same(billing, SettableFragmentFeature.For(app, "billing-ui").Fragment);
        Assert.Same(orders, SettableFragmentFeature.For(app, "orders-ui").Fragment);
    }

    [Theory]
    [InlineData("missing-ui")]
    [InlineData("ORDERS-UI")]
    public void For_throws_for_a_fragment_the_entry_does_not_list(string fragmentId)
    {
        AppDefinition app = TestApps.Entry(fragments: [TestApps.Fragment("orders-ui")]);

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => SettableFragmentFeature.For(app, fragmentId));

        Assert.Equal("fragmentId", thrown.ParamName);
    }

    [Fact]
    public void Setting_the_fragment_moves_the_feature_to_it()
    {
        SettableFragmentFeature feature = new(TestApps.Fragment("a-ui"));
        FragmentDefinition other = TestApps.Fragment("b-ui");

        feature.Fragment = other;

        Assert.Same(other, feature.Fragment);
    }
}
