using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Fixtures.Tests;

/// <summary>
/// The fixture documents and the contract schemas they answer to. Both trees are found from this
/// file's own path, as <c>tests/Contracts.Tests/ContractFiles.cs</c> does it, with the output
/// directory's ancestors as the fallback for a build that rewrote the path.
///
/// Every schema goes into one registry, as contracts/README.md -> "Using the schemas" requires: the
/// schemas <c>$ref</c> each other by <c>$id</c>, and <c>app-definition.schema.json</c> is almost
/// entirely <c>$ref</c>s into <c>registry.schema.json</c>, so loading it alone would resolve
/// nothing. All four are loaded rather than the two this suite evaluates, so a <c>$ref</c> added
/// later resolves instead of failing here.
/// </summary>
internal static class FixtureFiles
{
    private const string AppDefinitionSchema = "app-definition.schema.json";

    /// <summary>
    /// The registry document comes first: the others <c>$ref</c> its <c>$defs</c>, so it has to be
    /// in the registry before they are built.
    /// </summary>
    private static readonly string[] SchemaFiles =
    [
        "registry.schema.json",
        AppDefinitionSchema,
        "platform-config.schema.json",
        "infra-outputs.schema.json",
    ];

    private static readonly string Root = FindRepositoryRoot();

    private static readonly BuildOptions Build = new();

    private static readonly Dictionary<string, JsonSchema> Schemas = LoadSchemas();

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    /// <summary>Apps 1 and 2, which the apps root creates from the start.</summary>
    internal static IEnumerable<string> AppDefinitions => Definitions("apps");

    /// <summary>App 3, which stays out of the apps root until F-07 onboards it.</summary>
    internal static IEnumerable<string> OnboardingDefinitions => Definitions("onboarding");

    /// <summary>Every app definition, wherever it waits.</summary>
    internal static IEnumerable<string> AllDefinitions => AppDefinitions.Concat(OnboardingDefinitions);

    /// <summary>The user pool's resource servers and test users.</summary>
    internal static JsonNode CognitoPool => Read(Path("fixtures", "cognito", "pool.json"));

    /// <summary>
    /// The contract's own example of the task environment task 1.4 supplies. The bounds a schema
    /// cannot express are in its <c>Policy</c> and <c>Issuers</c> sections.
    /// </summary>
    internal static JsonNode PlatformConfiguration => Read(Path("contracts", "platform-config.example.json"));

    /// <summary>One definition, by the appId it carries.</summary>
    internal static JsonNode Definition(string appId) =>
        Read(AllDefinitions.FirstOrDefault(path => System.IO.Path.GetFileName(path) == appId + ".json")
             ?? throw new FileNotFoundException($"no definition for {appId}."));

    internal static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    /// <summary>A fixture document, parsed.</summary>
    internal static JsonNode Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"{path} parsed to JSON null.");

    /// <summary>Whether a definition validates, with the schema's own report when it does not.</summary>
    internal static (bool IsValid, string Report) ValidatesAsAppDefinition(JsonNode definition)
    {
        JsonElement instance = JsonSerializer.SerializeToElement(definition);
        EvaluationResults results = Schemas[AppDefinitionSchema].Evaluate(instance, Options);

        return (results.IsValid, Describe(results));
    }

    private static IEnumerable<string> Definitions(string directory) =>
        Directory.EnumerateFiles(Path("fixtures", directory), "*.json").Order(StringComparer.Ordinal);

    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        Dictionary<string, JsonSchema> schemas = [];
        foreach (string name in SchemaFiles)
        {
            JsonSchema schema = JsonSchema.FromText(File.ReadAllText(Path("contracts", name)), Build);
            Build.SchemaRegistry.Register(schema);
            schemas.Add(name, schema);
        }

        return schemas;
    }

    private static string Describe(EvaluationResults results)
    {
        StringBuilder report = new();
        Collect(results, report);

        return report.Length == 0 ? "(no detail)" : report.ToString();
    }

    private static void Collect(EvaluationResults results, StringBuilder report)
    {
        if (results.Errors is not null)
        {
            foreach (KeyValuePair<string, string> error in results.Errors)
            {
                report.Append(results.InstanceLocation)
                      .Append(" (")
                      .Append(error.Key)
                      .Append("): ")
                      .AppendLine(error.Value);
            }
        }

        foreach (EvaluationResults nested in results.Details ?? [])
        {
            Collect(nested, report);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string? thisFile = null)
    {
        // The source tree first: this file sits at fixtures/Fixtures.Tests/FixtureFiles.cs.
        string? fromSource = thisFile is null
            ? null
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", ".."));

        if (fromSource is not null && File.Exists(System.IO.Path.Combine(fromSource, "SpaPlatform.slnx")))
        {
            return fromSource;
        }

        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"the repository root was not found from '{thisFile}' or from '{AppContext.BaseDirectory}'.");
    }
}
