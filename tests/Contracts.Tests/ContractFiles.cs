using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Contracts.Tests;

/// <summary>
/// Loads everything under <c>contracts/</c>. Every schema goes into one build registry, because the
/// schemas <c>$ref</c> each other by <c>$id</c>: see contracts/README.md, "Using the schemas".
/// </summary>
internal static class ContractFiles
{
    public const string RegistrySchema = "registry.schema.json";
    public const string AppDefinitionSchema = "app-definition.schema.json";
    public const string PlatformConfigSchema = "platform-config.schema.json";
    public const string InfraOutputsSchema = "infra-outputs.schema.json";

    public const string RegistryExample = "registry.example.json";
    public const string AppDefinitionExample = "app-definition.example.json";
    public const string PlatformConfigExample = "platform-config.example.json";
    public const string InfraOutputsExample = "infra-outputs.example.json";

    /// <summary>Every file the contracts index promises, schema or prose.</summary>
    public static readonly string[] Files =
    [
        "README.md",
        RegistrySchema,
        RegistryExample,
        AppDefinitionSchema,
        AppDefinitionExample,
        PlatformConfigSchema,
        PlatformConfigExample,
        InfraOutputsSchema,
        InfraOutputsExample,
        "pipeline.md",
        "http-contract.md",
        "request-gate.md",
        "upstream-contract.md",
        "s3-layout.md",
        "telemetry.md",
        "resource-tags.md",
    ];

    /// <summary>
    /// The registry document comes first: the other three <c>$ref</c> its <c>$defs</c>, so it has to
    /// be in the registry before they are built.
    /// </summary>
    public static readonly string[] SchemaFiles =
        [RegistrySchema, AppDefinitionSchema, PlatformConfigSchema, InfraOutputsSchema];

    private static readonly string Directory = FindContractsDirectory();

    private static readonly BuildOptions Build = new();

    private static readonly Dictionary<string, JsonSchema> LoadedSchemas = LoadSchemas();

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public static string Path(string fileName) => System.IO.Path.Combine(Directory, fileName);

    public static string ReadText(string fileName) => File.ReadAllText(Path(fileName));

    public static JsonSchema Schema(string fileName) => LoadedSchemas[fileName];

    /// <summary>The schema file's own JSON, for assertions about annotations such as x-freeze.</summary>
    public static JsonNode SchemaSource(string fileName) => Example(fileName);

    /// <summary>The example document, parsed fresh so a caller may mutate it.</summary>
    public static JsonNode Example(string fileName) =>
        JsonNode.Parse(ReadText(fileName)) ?? throw new InvalidOperationException($"{fileName} parsed to JSON null.");

    public static (bool IsValid, string Report) Evaluate(string schemaFile, JsonNode? document)
    {
        JsonElement instance = JsonSerializer.SerializeToElement(document);
        EvaluationResults results = Schema(schemaFile).Evaluate(instance, Options);
        return (results.IsValid, Describe(results));
    }

    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        Dictionary<string, JsonSchema> schemas = [];
        foreach (string name in SchemaFiles)
        {
            JsonSchema schema = JsonSchema.FromText(ReadText(name), Build);
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

        if (results.Details is null)
        {
            return;
        }

        foreach (EvaluationResults nested in results.Details)
        {
            Collect(nested, report);
        }
    }

    private static string FindContractsDirectory([CallerFilePath] string? thisFile = null)
    {
        // The source tree first: this file sits at tests/Contracts.Tests/ContractFiles.cs.
        string? fromSource = thisFile is null
            ? null
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", "..", "contracts"));

        if (fromSource is not null && System.IO.Directory.Exists(fromSource))
        {
            return fromSource;
        }

        // Then the output directory's ancestors, for a deterministic build that rewrote the path above.
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return System.IO.Path.Combine(candidate.FullName, "contracts");
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"contracts/ was not found from '{thisFile}' or from '{AppContext.BaseDirectory}'.");
    }
}
