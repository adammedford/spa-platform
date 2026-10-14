namespace Gateway.ArchitectureTests;

/// <summary>
/// A-01 to A-04 against this solution. The rules themselves are proven in
/// <see cref="ReferenceRuleTests"/>, against graphs that break them; these four read the real
/// project files, and they are what CI fails on.
///
/// A-05, path ownership, is not here. It is a property of a change rather than of the tree, so
/// <c>.githooks/pre-push</c> and the CI step from task I-5 check it against a diff.
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void A01_the_gateway_never_references_the_test_identity_provider()
    {
        Assert.Empty(ReferenceRules.GatewayNeverReachesTheTestIdp(ProjectGraph.Solution));
    }

    [Fact]
    public void A02_the_mock_api_references_no_platform_project()
    {
        Assert.Empty(ReferenceRules.TheMockApiReferencesNoPlatformProject(ProjectGraph.Solution));
    }

    [Fact]
    public void A03_a_workstream_project_shares_only_abstractions()
    {
        Assert.Empty(ReferenceRules.WorkstreamsShareOnlyAbstractions(ProjectGraph.Solution));
    }

    [Fact]
    public void A03_the_gateway_references_every_workstream()
    {
        Assert.Empty(ReferenceRules.TheGatewayReferencesEveryWorkstream(ProjectGraph.Solution));
    }

    [Fact]
    public void A04_only_platform_identity_references_the_token_package()
    {
        Assert.Empty(ReferenceRules.OnlyIdentityReferencesTheTokenPackage(ProjectGraph.Solution));
    }

    [Fact]
    public void A04_is_read_from_package_references_that_were_actually_parsed()
    {
        // A-04 is vacuous in this tree: Platform.Identity is a stub and references no package at
        // all until task 3.8 adds the token-management one. So the rule passes today whether the
        // parser works or not, and it would keep passing if the parser silently returned nothing
        // after 3.8 lands. This asserts that package references are read, against two projects that
        // do have one.
        Assert.Contains("Yarp.ReverseProxy", ProjectGraph.Solution["Platform.Proxy"].PackageReferences);
        Assert.Contains("JsonSchema.Net", ProjectGraph.Solution["Contracts.Tests"].PackageReferences);
    }

    [Fact]
    public void Every_project_the_solution_lists_was_read()
    {
        // The rules are read from SpaPlatform.slnx, so a project missing from the solution is a
        // project no rule applies to. This is what stops A-01 to A-04 going quiet rather than red
        // when someone adds a project and forgets the solution.
        Assert.Equal(
            Directory
                .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.Contains("/bin/", StringComparison.Ordinal))
                .Where(path => !path.Contains("/obj/", StringComparison.Ordinal))
                .Where(path => !path.Contains("/spikes/", StringComparison.Ordinal))
                .Select(Path.GetFileNameWithoutExtension)
                .Order(StringComparer.Ordinal),
            ProjectGraph.Solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot => ProjectGraph.RepositoryRoot;
}
