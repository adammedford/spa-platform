using System.Text.Json.Nodes;

namespace Gateway.TestHarness;

/// <summary>
/// The workstream 5 fixtures the harness serves: the app definitions under <c>fixtures/apps/</c> and
/// <c>fixtures/onboarding/</c>, and the static SPAs under <c>fixtures/spa/</c>. Read from the
/// repository rather than copied, so the hermetic tier serves the same bytes the deployed tier
/// publishes.
/// </summary>
public static class FixtureFiles
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The repository root: the nearest directory above the test binaries that holds the solution.</summary>
    public static string RepositoryRoot => Root.Value;

    /// <summary>
    /// An app definition (contracts/app-definition.schema.json), from <c>fixtures/apps/</c> or, for an
    /// app that is onboarded during a test, <c>fixtures/onboarding/</c>.
    /// </summary>
    public static JsonObject AppDefinition(string appId)
    {
        foreach (string folder in new[] { "apps", "onboarding" })
        {
            string path = Path.Combine(RepositoryRoot, "fixtures", folder, appId + ".json");
            if (File.Exists(path))
            {
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            }
        }

        throw new FileNotFoundException($"no app definition for {appId} under fixtures/apps or fixtures/onboarding.");
    }

    /// <summary>Every file of one fixture SPA, keyed by its path relative to the release root, with '/' separators.</summary>
    public static IReadOnlyDictionary<string, byte[]> Spa(string appId)
    {
        string root = Path.Combine(RepositoryRoot, "fixtures", "spa", appId);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"no fixture SPA at fixtures/spa/{appId}.");
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SpaPlatform.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"no SpaPlatform.slnx above {AppContext.BaseDirectory}. The harness reads fixtures from the repository.");
    }
}
