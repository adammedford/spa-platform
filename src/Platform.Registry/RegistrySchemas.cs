using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Platform.Registry;

/// <summary>
/// The contract schemas, loaded once into one schema registry. The schemas <c>$ref</c> each other by
/// <c>$id</c> (<c>app-definition</c> reaches into <c>registry.schema.json#/$defs</c>), so every one
/// must be registered before any is evaluated: contracts/README.md, "Using the schemas".
///
/// The only file that touches the schema engine, so open decision D-1 in docs/dependencies.md (which
/// engine, and whether it stays in the gateway process) is a change here and nowhere else.
/// </summary>
internal static class RegistrySchemas
{
    public const string Registry = "registry.schema.json";
    public const string AppDefinition = "app-definition.schema.json";

    /// <summary>The registry document first: the other three <c>$ref</c> its <c>$defs</c>.</summary>
    private static readonly string[] Files =
        [Registry, AppDefinition, "platform-config.schema.json", "infra-outputs.schema.json"];

    private static readonly Dictionary<string, JsonSchema> Loaded = Load();

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.Hierarchical };

    /// <summary>Every schema violation, as a JSON pointer into the instance and the engine's message.</summary>
    public static ImmutableArray<RegistryIssue> Evaluate(string schemaFile, JsonElement instance)
    {
        EvaluationResults results = Loaded[schemaFile].Evaluate(instance, Options);
        if (results.IsValid)
        {
            return [];
        }

        ImmutableArray<RegistryIssue>.Builder issues = ImmutableArray.CreateBuilder<RegistryIssue>();
        Collect(results, issues);
        return issues.ToImmutable();
    }

    /// <summary>
    /// Hierarchical output, so a failure is reported only where its whole ancestry failed too. A failing
    /// node that carries no message of its own and has no failing child, such as a <c>not</c> whose
    /// subschema matched ('*' as a source), is reported by its schema location instead.
    /// </summary>
    private static void Collect(EvaluationResults results, ImmutableArray<RegistryIssue>.Builder issues)
    {
        int before = issues.Count;
        foreach (KeyValuePair<string, string> error in results.Errors ?? new Dictionary<string, string>())
        {
            issues.Add(new RegistryIssue(
                RegistryRules.Schema,
                results.InstanceLocation.ToString(),
                $"{error.Value} ({error.Key} at {results.EvaluationPath})"));
        }

        // Only into failing nodes: a passing "contains" or "not" has failing children by design (the
        // items that are not "openid", the pattern a source must not match), and they are not findings.
        foreach (EvaluationResults nested in results.Details ?? [])
        {
            if (!nested.IsValid)
            {
                Collect(nested, issues);
            }
        }

        if (issues.Count == before)
        {
            issues.Add(new RegistryIssue(
                RegistryRules.Schema,
                results.InstanceLocation.ToString(),
                $"The value fails the schema at {results.EvaluationPath}."));
        }
    }

    private static Dictionary<string, JsonSchema> Load()
    {
        BuildOptions build = new();
        Dictionary<string, JsonSchema> schemas = [];
        Assembly assembly = typeof(RegistrySchemas).Assembly;
        foreach (string file in Files)
        {
            // LogicalName in Platform.Registry.csproj; the copies under Schemas/ are the contract's bytes.
            using Stream stream = assembly.GetManifestResourceStream("contracts/" + file)
                ?? throw new InvalidOperationException($"contracts/{file} is not embedded in {assembly.GetName().Name}.");
            using StreamReader reader = new(stream);
            JsonSchema schema = JsonSchema.FromText(reader.ReadToEnd(), build);
            build.SchemaRegistry.Register(schema);
            schemas.Add(file, schema);
        }

        return schemas;
    }
}
