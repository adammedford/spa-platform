using System.Collections.Immutable;

namespace Gateway.ArchitectureTests;

/// <summary>
/// Each rule, fed a graph that breaks it. Without these the live assertions in
/// <see cref="ArchitectureTests"/> would pass against a rule that can never fail, and a broken rule
/// is indistinguishable from a clean repository.
/// </summary>
public sealed class ReferenceRuleTests
{
    [Fact]
    public void A01_catches_the_test_idp_reached_through_another_project()
    {
        // The direct reference is the easy case. This is the one worth a test: the gateway
        // references a helper, and the helper is what drags the test provider in.
        ProjectGraph graph = GraphOf(
            ("Gateway", ["Platform.Identity"], []),
            ("Platform.Identity", ["TestIdp"], []),
            ("TestIdp", [], []));

        Assert.NotEmpty(ReferenceRules.GatewayNeverReachesTheTestIdp(graph));
    }

    [Fact]
    public void A02_catches_a_platform_project_on_the_mock_api()
    {
        // The mock API exists to show that an upstream validates the token itself (assumption A5 and
        // criterion 7). One Platform reference and it is sharing the gateway's code instead.
        ProjectGraph graph = GraphOf(
            ("MockApi", ["Platform.Abstractions"], []),
            ("Platform.Abstractions", [], []));

        Assert.NotEmpty(ReferenceRules.TheMockApiReferencesNoPlatformProject(graph));
    }

    [Fact]
    public void A02_allows_the_mock_api_its_own_references()
    {
        ProjectGraph graph = GraphOf(("MockApi", [], ["Microsoft.AspNetCore.Authentication.JwtBearer"]));

        Assert.Empty(ReferenceRules.TheMockApiReferencesNoPlatformProject(graph));
    }

    [Fact]
    public void A03_catches_one_workstream_referencing_another()
    {
        ProjectGraph graph = GraphOf(
            ("Platform.Proxy", ["Platform.Abstractions", "Platform.Identity"], []),
            ("Platform.Identity", ["Platform.Abstractions"], []),
            ("Platform.Abstractions", [], []));

        Assert.NotEmpty(ReferenceRules.WorkstreamsShareOnlyAbstractions(graph));
    }

    [Fact]
    public void A03_catches_a_workstream_that_does_not_reference_abstractions()
    {
        // The other half of the rule. A workstream project that shares nothing is not following the
        // contract; it is ignoring it.
        ProjectGraph graph = GraphOf(
            ("Platform.Proxy", [], []),
            ("Platform.Abstractions", [], []));

        Assert.NotEmpty(ReferenceRules.WorkstreamsShareOnlyAbstractions(graph));
    }

    [Fact]
    public void A03_catches_a_workstream_reached_through_abstractions()
    {
        // Abstractions is the one shared project, so a workstream project reached through it would
        // put every workstream in every other one's closure without a single direct reference.
        ProjectGraph graph = GraphOf(
            ("Platform.Proxy", ["Platform.Abstractions"], []),
            ("Platform.Abstractions", ["Platform.Identity"], []),
            ("Platform.Identity", [], []));

        Assert.NotEmpty(ReferenceRules.WorkstreamsShareOnlyAbstractions(graph));
    }

    [Fact]
    public void A04_catches_the_token_package_outside_identity()
    {
        ProjectGraph graph = GraphOf(
            ("Platform.Identity", [], ["Duende.AccessTokenManagement.OpenIdConnect"]),
            ("Platform.Proxy", [], ["Duende.AccessTokenManagement.OpenIdConnect"]));

        Assert.NotEmpty(ReferenceRules.OnlyIdentityReferencesTheTokenPackage(graph));
    }

    [Fact]
    public void A04_allows_identity_its_own_token_package()
    {
        ProjectGraph graph = GraphOf(
            ("Platform.Identity", [], ["Duende.AccessTokenManagement.OpenIdConnect"]),
            ("Platform.Proxy", [], ["Yarp.ReverseProxy"]));

        Assert.Empty(ReferenceRules.OnlyIdentityReferencesTheTokenPackage(graph));
    }

    private static ProjectGraph GraphOf(params (string Name, string[] Projects, string[] Packages)[] projects) =>
        new(projects.Select(project => new Project(
            project.Name,
            $"src/{project.Name}/{project.Name}.csproj",
            [.. project.Projects],
            [.. project.Packages])));
}
