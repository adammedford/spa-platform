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
/// The only file that touches the schema engine, so swapping the engine is a change here and nowhere
/// else. Which engine is decided: JsonSchema.Net 8.0.5, running in the gateway process through this
/// project (docs/dependencies.md -> "Decided", D-1, which records what would reopen it).
/// </summary>
internal static class RegistrySchemas
{
    public const string Registry = "registry.schema.json";
    public const string AppDefinition = "app-definition.schema.json";
    public const string PlatformConfiguration = "platform-config.schema.json";
    public const string InfraOutputs = "infra-outputs.schema.json";

    /// <summary>Where every contract schema's <c>$id</c> lives (contracts/README.md, "Using the schemas").</summary>
    private const string IdBase = "https://spa-platform.internal/contracts/";

    /// <summary>The registry document first: the other three <c>$ref</c> its <c>$defs</c>.</summary>
    private static readonly string[] Files =
        [Registry, AppDefinition, PlatformConfiguration, InfraOutputs];

    /// <summary>
    /// The one schema registry. Declared before <see cref="Loaded"/>, because static initializers run in
    /// textual order and <see cref="Load"/> registers into it.
    /// </summary>
    private static readonly BuildOptions Build = new();

    private static readonly Dictionary<string, JsonSchema> Loaded = Load();

    /// <summary>The <c>$defs</c> evaluated on their own, built on first use and kept.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonSchema> Definitions = new(StringComparer.Ordinal);

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.Hierarchical };

    /// <summary>Every schema violation, as a JSON pointer into the instance and the engine's message.</summary>
    public static ImmutableArray<RegistryIssue> Evaluate(string schemaFile, JsonElement instance) =>
        Issues(Loaded[schemaFile].Evaluate(instance, Options));

    /// <summary>
    /// Whether a value satisfies one of <c>registry.schema.json</c>'s <c>$defs</c>, such as <c>appId</c>.
    /// The definition is reached by <c>$ref</c> through the same registry, so the rule exists once.
    /// </summary>
    public static bool SatisfiesRegistryDefinition(string definition, JsonElement instance)
    {
        JsonSchema schema = Definitions.GetOrAdd(definition, name =>
            JsonSchema.FromText($"{{\"$ref\":\"{IdBase}{Registry}#/$defs/{name}\"}}", Build));
        return schema.Evaluate(instance, Options).IsValid;
    }

    private static ImmutableArray<RegistryIssue> Issues(EvaluationResults results)
    {
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
        Dictionary<string, JsonSchema> schemas = [];
        Assembly assembly = typeof(RegistrySchemas).Assembly;
        foreach (string file in Files)
        {
            // LogicalName in Platform.Registry.csproj, where contracts/*.schema.json is linked: the
            // resource is the contract's own bytes, not a copy of them.
            using Stream stream = assembly.GetManifestResourceStream("contracts/" + file)
                ?? throw new InvalidOperationException($"contracts/{file} is not embedded in {assembly.GetName().Name}.");
            using StreamReader reader = new(stream);
            JsonSchema schema = JsonSchema.FromText(reader.ReadToEnd(), Build);
            Build.SchemaRegistry.Register(schema);
            schemas.Add(file, schema);
        }

        return schemas;
    }
}
