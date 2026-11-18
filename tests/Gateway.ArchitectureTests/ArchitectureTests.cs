namespace Gateway.ArchitectureTests;

/// <summary>
/// A-01 to A-04 against this solution. The rules themselves are proven in
/// <see cref="ReferenceRuleTests"/>, against graphs that break them; these four read the real
/// project files, and they are what CI fails on.
///
/// The rule-id rule likewise: <see cref="RuleIdLiterals"/> is proven in
/// <see cref="RuleIdLiteralTests"/>, and its live assertion here reads the workstream projects' own
/// sources. It carries no <c>A-</c> id, because the plan's test table is frozen and a number minted
/// outside it would be a copy of nothing (#154).
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
        // A-04 asks that no project but Platform.Identity names the token-management package, so it
        // passes on an empty parse: a parser that silently returned no package references would
        // leave the rule green. This asserts that package references are read. The first line is
        // the case A-04 is about, and it goes through the rule's own constant, so the name the rule
        // looks for is proven to be the one Platform.Identity's project file spells; the other two
        // are projects outside the rule that also have one.
        Assert.Contains(ReferenceRules.TokenManagementPackage, ProjectGraph.Solution[ReferenceRules.Identity].PackageReferences);
        Assert.Contains("Yarp.ReverseProxy", ProjectGraph.Solution["Platform.Proxy"].PackageReferences);
        Assert.Contains("JsonSchema.Net", ProjectGraph.Solution["Contracts.Tests"].PackageReferences);
    }

    [Fact]
    public void Every_project_the_solution_lists_was_read()
    {
        // The rules are read from SpaPlatform.slnx, so a project missing from the solution is a
        // project no rule applies to. This is what stops A-01 to A-04 going quiet rather than red
        // when someone adds a project and forgets the solution. .scratch/ is left out of the scan,
        // with bin/, obj/ and spikes/: overnight runs write probes under .scratch/<slug>/log/, which
        // git ignores, and a project file there failed this in the main checkout (#1274).
        // .claude/worktrees/ is left out too: an agent's worktree there, also ignored, holds a copy
        // of every project, so each name was counted twice in the main checkout (#1374).
        Assert.Equal(
            ProjectGraph.ProjectsATreeHolds(
                RepositoryRoot,
                Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)),
            ProjectGraph.Solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_project_file_under_scratch_is_no_project_the_solution_must_list()
    {
        // The filter of the assertion above, on paths this test supplies, so nothing is written
        // into the repository's tree: the probe an overnight run left is not counted, and neither
        // is build output or a spike.
        Assert.Equal(
            ["Gateway"],
            ProjectGraph.ProjectsATreeHolds(
                "/repo",
                [
                    "/repo/src/Gateway/Gateway.csproj",
                    "/repo/.scratch/overnight/log/probe/probe.csproj",
                    "/repo/.scratch/overnight/log/issue-976-probe/tools/bench/floor/Bench.Floor.csproj",
                    "/repo/src/Gateway/obj/Gateway.csproj",
                    "/repo/src/Gateway/bin/Debug/Gateway.csproj",
                    "/repo/spikes/g0/cognito/Cognito.csproj",
                ]));
    }

    [Fact]
    public void A_project_file_in_an_agent_worktree_is_no_project_the_solution_must_list()
    {
        // The filter again: a copy of the gateway in an agent's worktree under .claude/worktrees/
        // is not counted beside the main checkout's own (#1374).
        // Mutation (#1374): remove "/.claude/worktrees/" from OutsideTheSolution and this reads
        // ["Gateway", "Gateway"].
        Assert.Equal(
            ["Gateway"],
            ProjectGraph.ProjectsATreeHolds(
                "/repo",
                [
                    "/repo/src/Gateway/Gateway.csproj",
                    "/repo/.claude/worktrees/lane/src/Gateway/Gateway.csproj",
                ]));
    }

    [Fact]
    public void A_checkout_inside_an_agent_worktree_counts_its_own_projects()
    {
        // The suite also runs from inside such a worktree, where every absolute path holds
        // /.claude/worktrees/. The filter reads only the part of a path below the root, so the
        // worktree's own projects are counted.
        // Mutation (#1374): match the absolute path instead of the root-relative one and this reads
        // an empty list.
        Assert.Equal(
            ["Gateway", "Platform.Proxy"],
            ProjectGraph.ProjectsATreeHolds(
                "/repo/.claude/worktrees/lane",
                [
                    "/repo/.claude/worktrees/lane/src/Gateway/Gateway.csproj",
                    "/repo/.claude/worktrees/lane/src/Platform.Proxy/Platform.Proxy.csproj",
                ]));
    }

    [Fact]
    public void A_project_file_anywhere_else_is_one_the_solution_must_list()
    {
        // What the assertion is for: a project added beside the others and left out of the solution
        // is named, whatever directory it is in, a dot-directory that is not .scratch/ included, and
        // a directory under .claude/ that is not worktrees/.
        // Mutation (#1374): widen "/.claude/worktrees/" in OutsideTheSolution to "/.claude/" and this
        // fails 1 of 27, without Agent.
        Assert.Equal(
            ["Agent", "Forgotten", "Gateway", "Hidden", "Scratch"],
            ProjectGraph.ProjectsATreeHolds(
                "/repo",
                [
                    "/repo/src/Gateway/Gateway.csproj",
                    "/repo/tools/forgotten/Forgotten.csproj",
                    "/repo/.github/hidden/Hidden.csproj",
                    "/repo/tools/scratch/Scratch.csproj",
                    "/repo/.claude/workflows/agent/Agent.csproj",
                ]));
    }

    [Fact]
    public void No_workstream_project_spells_a_rule_id_or_a_problem_code_as_a_literal()
    {
        // The six projects A-03 binds, so Hosting, Registry and Telemetry are covered by construction
        // and Platform.Abstractions, where the literals belong, is not. Test projects are left out on
        // purpose: see RuleIdLiterals. The ids are the rule ids and the problem codes (#158).
        Assert.Empty(RuleIdLiterals.Violations(ReferenceRules.WorkstreamProjects.Select(DirectoryOf), RuleIdLiterals.Ids));
    }

    [Fact]
    public void The_rule_id_scan_finds_the_literals_platform_abstractions_holds()
    {
        // The live assertion above passes over a tree with no literal whether or not the scan read a
        // file, the hazard A04_is_read_from_package_references_that_were_actually_parsed answers. So the
        // same rule runs here over the one project that must hold the literals: the fourteen declarations
        // in RuleIds (auth.store since #343's item 2, auth.chain since the fragments spec, #961, typed
        // by #988 with the Rule ids row of #983), the fifteen code declarations in Problems (#158;
        // shell_unavailable since #427, session_store_unavailable since #343's item 2, the thirteenth,
        // store_unavailable since #543, the fourteenth by landing, declared after idp_unavailable, and
        // fragment_login_required since #961, typed by #988 with the Errors row of #982, the fifteenth
        // by landing, declared after session_ended), and the one quoted id in
        // IRequestTelemetryFeature's doc comment, which proves the scan reads comments too. No doc
        // comment in Platform.Abstractions quotes a code: Problems names each in its declaration only.
        // The count moves with its subject: a quoted id or code added to or removed from
        // Platform.Abstractions moves it.
        // Mutation (#1002): remove only the AuthChain entry from RuleIds.All and the
        // FragmentLoginRequired entry from Problems.Rows, keeping the constants, and auth.chain and
        // fragment_login_required leave the ids the scan looks for: the count reads 28, RuleIds.cs 13
        // and Problems.cs 14. Rejection names RuleIds.AuthChain, so dropping the constant does not
        // compile.
        string[] hits = [.. RuleIdLiterals.Violations([DirectoryOf(ReferenceRules.Abstractions)], RuleIdLiterals.Ids)];

        Assert.Equal(30, hits.Length);
        Assert.Equal(
            [
                ("Platform.Abstractions/IRequestTelemetryFeature.cs", 1),
                ("Platform.Abstractions/Problems.cs", 15),
                ("Platform.Abstractions/RuleIds.cs", 14),
            ],
            hits
                .GroupBy(hit => hit[..hit.IndexOf(':', StringComparison.Ordinal)], StringComparer.Ordinal)
                .Select(file => (file.Key, file.Count()))
                .Order());
    }

    private static string RepositoryRoot => ProjectGraph.RepositoryRoot;

    /// <summary>
    /// A project's directory, from where the solution says its project file is rather than from an
    /// assumed <c>src/</c> layout.
    /// </summary>
    private static string DirectoryOf(string project) =>
        Path.GetDirectoryName(Path.Combine(RepositoryRoot, ProjectGraph.Solution[project].Path))!;
}
