using System.Collections.Immutable;
using System.Globalization;
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
    /// The one schema registry, this class's own rather than the engine's <c>SchemaRegistry.Global</c>,
    /// which <c>BuildOptions</c> uses by default and any other code in the process may build into.
    /// Declared before <see cref="Loaded"/>, because static initializers run in textual order and
    /// <see cref="Load"/> registers into it.
    ///
    /// Written only while this type initializes, which the runtime runs once on one thread, and only
    /// read after it (issue #373). The engine's registry is a plain <c>Dictionary</c> with no lock
    /// (https://raw.githubusercontent.com/json-everything/json-everything/3520d7ac43e5c6c9b91abeac5af992eb82ffbf63/src/JsonSchema/SchemaRegistry.cs,
    /// the commit JsonSchema.Net 8.0.5's SourceLink names), and every <c>JsonSchema.Build</c> registers
    /// the schema it builds before it resolves that schema's <c>$ref</c>s through the same registry
    /// (.../src/JsonSchema/JsonSchema.cs, <c>Build</c>). A <c>Dictionary</c> "can support multiple
    /// readers concurrently, as long as the collection is not modified"
    /// (https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2#thread-safety),
    /// so a build after initialization, racing another, corrupts it.
    /// </summary>
    private static readonly BuildOptions Build = new() { SchemaRegistry = new SchemaRegistry() };

    private static readonly Dictionary<string, JsonSchema> Loaded = Load();

    /// <summary>
    /// Every one of <c>registry.schema.json</c>'s <c>$defs</c>, each as a <c>$ref</c> to it, built here
    /// with the rest rather than on first use: two first uses at once were two builds at once, which is
    /// how <c>Spactl.Tests</c> failed under the full run (issue #373). Declared after
    /// <see cref="Loaded"/>, whose registration the <c>$ref</c>s resolve through.
    /// </summary>
    private static readonly Dictionary<string, JsonSchema> Definitions = BuildDefinitions();

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.Hierarchical };

    /// <summary>
    /// Where <c>apis[].timeoutSeconds</c> is defined, as a pointer into <c>registry.schema.json</c>.
    /// <c>app-definition.schema.json</c> reaches the same definition by <c>$ref</c>, so the engine reports
    /// a failure of it under this one location whichever of the two documents was evaluated: an output
    /// unit's schema location is dereferenced and uses the canonical URI of the schema resource
    /// (https://json-schema.org/draft/2020-12/json-schema-core#section-12.3.2).
    /// </summary>
    private const string TimeoutSecondsPointer = "/$defs/api/properties/timeoutSeconds";

    private const string TimeoutSecondsLocation = IdBase + Registry + "#" + TimeoutSecondsPointer;

    /// <summary>https://json-schema.org/draft/2020-12/json-schema-validation#section-6.2.2</summary>
    private const string MaximumKeyword = "maximum";

    /// <summary>
    /// The contract's <c>maximum</c> on <c>apis[].timeoutSeconds</c>, the longest timeout the proxy
    /// applies as written (ADR-0019 decision 3). Read from the schema's own bytes: the contract states
    /// the bound and the proxy proves it (ADR-0015 decision 3), A-03 keeps <c>Platform.Proxy</c>'s
    /// measured constant out of this project's reach, and a copied number would agree with the contract
    /// only for as long as someone remembered to edit both.
    /// </summary>
    private static readonly long TimeoutSecondsMaximum = ReadMaximum(Registry, TimeoutSecondsPointer);

    private static readonly string TimeoutCeilingMessage =
        $"timeoutSeconds is above {TimeoutSecondsMaximum.ToString(CultureInfo.InvariantCulture)}, the contract's maximum: " +
        "the longest timeout in seconds the proxy applies as written. Above it the proxy would silently apply " +
        "a different limit from the one the registry states (ADR-0019).";

    /// <summary>
    /// Every schema violation, as a JSON pointer into the instance and the engine's message. A value above
    /// the <c>timeoutSeconds</c> maximum is reported as <see cref="RegistryRules.IntegerOutOfRange"/>
    /// instead; every other violation as <see cref="RegistryRules.Schema"/>.
    /// </summary>
    public static ImmutableArray<RegistryIssue> Evaluate(string schemaFile, JsonElement instance) =>
        Issues(Loaded[schemaFile].Evaluate(instance, Options));

    /// <summary>
    /// Whether a value satisfies one of <c>registry.schema.json</c>'s <c>$defs</c>, such as <c>appId</c>.
    /// The definition is reached by <c>$ref</c> through the same registry, so the rule exists once.
    /// </summary>
    public static bool SatisfiesRegistryDefinition(string definition, JsonElement instance) =>
        Definitions.TryGetValue(definition, out JsonSchema? schema)
            ? schema.Evaluate(instance, Options).IsValid
            : throw new ArgumentOutOfRangeException(nameof(definition), definition, $"contracts/{Registry} has no $defs/{definition}.");

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
            issues.Add(IsTimeoutCeiling(results, error.Key)
                ? new RegistryIssue(RegistryRules.IntegerOutOfRange, results.InstanceLocation.ToString(), TimeoutCeilingMessage)
                : new RegistryIssue(
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

    /// <summary>
    /// A <c>maximum</c> failure on <c>apis[].timeoutSeconds</c>. That bound is not a rule of the document
    /// format but the platform's: contracts/README.md, "A schema-valid integer the platform cannot hold or
    /// apply", the row a <c>revision</c> beyond Int64 is reported under too.
    /// </summary>
    private static bool IsTimeoutCeiling(EvaluationResults node, string keyword) =>
        keyword == MaximumKeyword && string.Equals(node.SchemaLocation.OriginalString, TimeoutSecondsLocation, StringComparison.Ordinal);

    private static Dictionary<string, JsonSchema> Load()
    {
        Dictionary<string, JsonSchema> schemas = [];
        foreach (string file in Files)
        {
            JsonSchema schema = JsonSchema.FromText(Text(file), Build);
            Build.SchemaRegistry.Register(schema);
            schemas.Add(file, schema);
        }

        return schemas;
    }

    private static Dictionary<string, JsonSchema> BuildDefinitions()
    {
        using JsonDocument schema = JsonDocument.Parse(Text(Registry));
        Dictionary<string, JsonSchema> definitions = new(StringComparer.Ordinal);
        foreach (JsonProperty definition in schema.RootElement.GetProperty("$defs").EnumerateObject())
        {
            string reference = JsonSerializer.Serialize($"{IdBase}{Registry}#/$defs/{definition.Name}");
            definitions.Add(definition.Name, JsonSchema.FromText($"{{\"$ref\":{reference}}}", Build));
        }

        return definitions;
    }

    /// <summary>The integer <c>maximum</c> of the subschema at <paramref name="pointer"/> in a contract schema.</summary>
    private static long ReadMaximum(string file, string pointer)
    {
        using JsonDocument schema = JsonDocument.Parse(Text(file));
        JsonElement node = schema.RootElement;
        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
            {
                break;
            }
        }

        return node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(MaximumKeyword, out JsonElement maximum)
            && maximum.TryGetInt64(out long value)
                ? value
                : throw new InvalidOperationException(
                    $"contracts/{file} has no integer maximum at {pointer}, which the validator reports as {RegistryRules.IntegerOutOfRange} (ADR-0019).");
    }

    private static string Text(string file)
    {
        Assembly assembly = typeof(RegistrySchemas).Assembly;
        // LogicalName in Platform.Registry.csproj, where contracts/*.schema.json is linked: the
        // resource is the contract's own bytes, not a copy of them.
        using Stream stream = assembly.GetManifestResourceStream("contracts/" + file)
            ?? throw new InvalidOperationException($"contracts/{file} is not embedded in {assembly.GetName().Name}.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
