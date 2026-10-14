namespace Gateway.ArchitectureTests;

/// <summary>
/// A-01 to A-04 as rules over a reference graph rather than assertions about this repository. Each
/// returns the violations it found, empty when the rule holds.
///
/// The separation is what makes the rules worth having. Asserted straight against a tree that
/// already satisfies them, a rule that can never fail and a repository that is clean look exactly
/// alike; ReferenceRuleTests feeds each rule a graph that breaks it.
/// </summary>
public static class ReferenceRules
{
    /// <summary>The project whose references the gateway's own closure is read from.</summary>
    public const string Gateway = "Gateway";

    /// <summary>The one project the architecture forbids the gateway to reach (A-01).</summary>
    public const string TestIdp = "TestIdp";

    /// <summary>The mock API, which proves an upstream validates tokens itself (A-02).</summary>
    public const string MockApi = "MockApi";

    /// <summary>The contract every workstream shares, and the only one it may share (A-03).</summary>
    public const string Abstractions = "Platform.Abstractions";

    /// <summary>The token-management package, which belongs to one project (A-04).</summary>
    public const string TokenManagementPackage = "Duende.AccessTokenManagement.OpenIdConnect";

    /// <summary>The one project that may reference <see cref="TokenManagementPackage"/> (A-04).</summary>
    public const string Identity = "Platform.Identity";

    /// <summary>
    /// The six projects a workstream owns. Platform.Abstractions and Platform.Testing are the
    /// integrator's, so neither is bound by A-03's "and no other workstream's project".
    /// </summary>
    public static readonly string[] WorkstreamProjects =
    [
        "Platform.Registry",
        "Platform.Hosting",
        "Platform.Identity",
        "Platform.Proxy",
        "Platform.Security",
        "Platform.Telemetry",
    ];

    /// <summary>
    /// A-01: the gateway never references the test identity provider. The closure and not the
    /// direct list, because a provider reached through one more project is still compiled in.
    ///
    /// The test projects are deliberately out of scope: workstream 6's harness wires the test
    /// provider on purpose, and that is the point of having one.
    /// </summary>
    public static IEnumerable<string> GatewayNeverReachesTheTestIdp(ProjectGraph graph) =>
        graph.ClosureOf(Gateway).Contains(TestIdp)
            ? [$"{Gateway} reaches {TestIdp} through {string.Join(" -> ", PathTo(graph, Gateway, TestIdp))}."]
            : [];

    /// <summary>One route from a project to something it should not reach, for the message.</summary>
    private static IEnumerable<string> PathTo(ProjectGraph graph, string from, string to)
    {
        Dictionary<string, string> cameFrom = [];
        Queue<string> pending = new([from]);

        while (pending.TryDequeue(out string? current))
        {
            foreach (string next in graph.Contains(current) ? graph[current].ProjectReferences : [])
            {
                if (cameFrom.TryAdd(next, current) && next != to)
                {
                    pending.Enqueue(next);
                }
            }
        }

        List<string> route = [to];
        for (string step = to; cameFrom.TryGetValue(step, out string? previous); step = previous)
        {
            route.Insert(0, previous);
        }

        return route;
    }

    /// <summary>
    /// A-02: the mock API references no <c>Platform.*</c> project. It stands for an upstream API,
    /// and an upstream that shared the gateway's code would prove nothing about assumption A5, that
    /// upstreams validate bearer tokens themselves.
    /// </summary>
    public static IEnumerable<string> TheMockApiReferencesNoPlatformProject(ProjectGraph graph) =>
        graph.Contains(MockApi)
            ? graph.ClosureOf(MockApi)
                .Where(reference => reference.StartsWith("Platform.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(reference => $"{MockApi} references {reference}.")
            : [];

    /// <summary>
    /// A-03: a workstream project references <see cref="Abstractions"/> and no other workstream's
    /// project. Only the gateway references them all.
    ///
    /// The closure again, and for a sharper reason than A-01: Abstractions is the one project every
    /// workstream shares, so a workstream project reached through it would put every workstream in
    /// every other one's closure without a single direct reference appearing anywhere.
    /// </summary>
    public static IEnumerable<string> WorkstreamsShareOnlyAbstractions(ProjectGraph graph)
    {
        foreach (string workstream in WorkstreamProjects.Where(graph.Contains))
        {
            IReadOnlyCollection<string> reached = graph.ClosureOf(workstream);

            if (!reached.Contains(Abstractions))
            {
                yield return $"{workstream} does not reference {Abstractions}.";
            }

            foreach (string other in reached
                .Where(reference => reference != workstream && WorkstreamProjects.Contains(reference))
                .Order(StringComparer.Ordinal))
            {
                yield return $"{workstream} reaches another workstream's project, {other}.";
            }
        }
    }

    /// <summary>
    /// The other half of A-03: "Only <c>Gateway</c> references them all." Returns the workstream
    /// projects the gateway does not reference, because a composition root that leaves one out is a
    /// stage of the pipeline that is not composed.
    /// </summary>
    public static IEnumerable<string> TheGatewayReferencesEveryWorkstream(ProjectGraph graph) =>
        WorkstreamProjects
            .Where(workstream => graph.Contains(workstream) && !graph[Gateway].ProjectReferences.Contains(workstream))
            .Select(workstream => $"{Gateway} does not reference {workstream}.");

    /// <summary>
    /// A-04: only <see cref="Identity"/> references the token-management package. Refresh, rotation
    /// and revocation live behind <c>IAccessTokenSource</c>, and a second project naming the package
    /// is a second place that could ask a provider for a token.
    ///
    /// Direct references only, and that is the rule rather than a shortcut: the package is a
    /// transitive dependency of itself through Platform.Identity, so a closure would flag every
    /// project that legitimately references Platform.Identity, which is the gateway.
    /// </summary>
    public static IEnumerable<string> OnlyIdentityReferencesTheTokenPackage(ProjectGraph graph) =>
        graph.Projects
            .Where(project => project.Name != Identity && project.PackageReferences.Contains(TokenManagementPackage))
            .Select(project => $"{project.Path} references {TokenManagementPackage}.");
}
