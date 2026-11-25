using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The registry entry builder's fragment half (#988 part 12; the fragments spec, #961): an entry
/// keeps the fragments it is given, in order, and a fragment's defaults never collide with a shell's.
/// </summary>
public sealed class TestAppsTests
{
    [Fact]
    public void An_entry_with_no_fragments_named_has_none()
    {
        Assert.Empty(TestApps.Entry().Fragments);
    }

    [Fact]
    public void An_entry_keeps_its_fragments_in_the_order_given()
    {
        // Mutation: sort the fragments, or drop the Fragments initialiser.
        FragmentDefinition second = TestApps.Fragment("b-ui");
        FragmentDefinition first = TestApps.Fragment("a-ui");

        AppDefinition entry = TestApps.Entry(fragments: [second, first]);

        Assert.Equal([second, first], entry.Fragments);
    }

    [Fact]
    public void A_fragments_default_client_id_is_shared_by_no_shell_default()
    {
        // The validator requires every client id in a registry to be distinct (#961).
        FragmentDefinition fragment = TestApps.Fragment("frag1", appId: "app1");

        Assert.Equal("client-app1-frag1", fragment.OAuth.ClientId);
        Assert.NotEqual(TestApps.Entry("app1").OAuth.ClientId, fragment.OAuth.ClientId);
        Assert.NotEqual(TestApps.Fragment("frag2", appId: "app1").OAuth.ClientId, fragment.OAuth.ClientId);
        Assert.Equal("ssm:/spa-platform/poc/apps/app1/fragments/frag1/client-secret:1", fragment.OAuth.ClientSecretRef);
    }

    [Fact]
    public void A_fragments_entry_defaults_to_the_default_entry_module()
    {
        Assert.Equal(PlatformPaths.DefaultEntry, TestApps.Fragment().Entry);
        Assert.Equal("entry.js", TestApps.Fragment().Entry);
    }
}
