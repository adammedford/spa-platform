using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Platform.Registry;

/// <summary>
/// One reason the platform configuration fails <c>contracts/platform-config.schema.json</c>.
/// <paramref name="Key"/> is a configuration key in its colon form (<c>Session:MaxSessions</c>), and
/// <paramref name="Keyword"/> the schema keyword that failed (<c>minimum</c>, <c>required</c>), empty when
/// the failing subschema names none. Neither ever carries a configured value (ADR-0042 decision 1).
/// </summary>
public sealed record PlatformConfigurationFinding(string Key, string Keyword)
{
    public override string ToString() => Keyword.Length == 0 ? Key : $"{Key} ({Keyword})";
}

/// <summary>
/// The start-up check ADR-0042 decides: the gateway's <see cref="IConfiguration"/>, rebuilt into the
/// JSON form <c>contracts/platform-config.schema.json</c> describes, evaluated against that schema with
/// the engine <see cref="ContractDocuments.ValidatePlatformConfiguration"/> uses for <c>spactl</c>.
///
/// The rebuild is typed by the schema (#1400). In the deployed, environment-variable form every value
/// is a string (<c>Session__MaxSessions=10000</c>) and an array is numbered keys, so a value is read as
/// an integer only where the schema's property says <c>integer</c>, and a section is an array only where
/// it says <c>array</c>. A value that does not parse as the schema's integer stays a string, so the
/// schema's own <c>type</c> refuses it. Only the root properties the schema names are rebuilt: what else
/// the environment carries (<c>Logging</c>, <c>ASPNETCORE_*</c>) is not the platform's, and the schema's
/// root admits it anyway. Inside a section the schema names, every key is rebuilt, so a section that
/// is closed (<c>additionalProperties: false</c>) refuses a key it does not know.
/// </summary>
public static class PlatformConfiguration
{
    private const string SchemaFile = RegistrySchemas.PlatformConfiguration;

    /// <summary>Every contract schema, parsed once, by file name, for the rebuild's <c>$ref</c>s and <c>required</c>s.</summary>
    private static readonly Dictionary<string, JsonElement> Schemas = LoadSchemas();

    /// <summary>The configuration's JSON form, as the schema is evaluated against it.</summary>
    public static JsonObject Document(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        JsonElement root = Schemas[SchemaFile];
        JsonObject document = [];
        foreach (JsonProperty property in root.GetProperty("properties").EnumerateObject())
        {
            IConfigurationSection section = configuration.GetSection(property.Name);
            if (Present(section))
            {
                document[property.Name] = Node(section, property.Value, SchemaFile);
            }
        }

        return document;
    }

    /// <summary>Every finding, empty when the configuration satisfies the schema.</summary>
    public static ImmutableArray<PlatformConfigurationFinding> Findings(IConfiguration configuration)
    {
        JsonObject document = Document(configuration);
        JsonElement instance = JsonSerializer.SerializeToElement(document);
        ImmutableArray<PlatformConfigurationFinding>.Builder findings = ImmutableArray.CreateBuilder<PlatformConfigurationFinding>();
        foreach (SchemaFailure failure in RegistrySchemas.Failures(SchemaFile, instance))
        {
            IReadOnlyList<string> location = Segments(failure.InstanceLocation);
            IReadOnlyList<string[]> missing = failure.Keyword == "required"
                ? Missing(failure.EvaluationPath, instance, location)
                : [];

            // A node with no keyword of its own is a false schema, the one additionalProperties: false
            // stands for: the last segment of its evaluation path names the keyword that put it there.
            string keyword = failure.Keyword.Length > 0 ? failure.Keyword : Segments(failure.EvaluationPath).LastOrDefault() ?? "";
            if (missing.Count == 0)
            {
                Add(findings, new PlatformConfigurationFinding(ConfigurationPath.Combine(location), keyword));
            }

            foreach (string[] key in missing)
            {
                Add(findings, new PlatformConfigurationFinding(ConfigurationPath.Combine([.. location, .. key]), keyword));
            }
        }

        return findings.ToImmutable();
    }

    private static void Add(ImmutableArray<PlatformConfigurationFinding>.Builder findings, PlatformConfigurationFinding finding)
    {
        // A $ref and the subschema it reaches can both report one failure: once is enough.
        if (!findings.Contains(finding))
        {
            findings.Add(finding);
        }
    }

    /// <summary>
    /// A section that holds anything. A key whose value is null, as a later in-memory source writes to
    /// take back an earlier one, says no more than an absent key, so the schema's <c>required</c> names
    /// it rather than its <c>type</c> refusing a JSON null.
    /// </summary>
    private static bool Present(IConfigurationSection section) => section.Value is not null || section.GetChildren().Any();

    /// <summary>
    /// One section as the JSON value <paramref name="schema"/> describes, or, where no schema describes
    /// it, as strings and objects: the schema then refuses it for being there.
    /// </summary>
    private static JsonNode? Node(IConfigurationSection section, JsonElement? schema, string file)
    {
        (JsonElement? resolved, string resolvedFile) = schema is { } given ? Resolve(given, file) : (null, file);
        string? type = resolved is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("type", out JsonElement t)
            && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        IConfigurationSection[] children = [.. section.GetChildren().Where(Present)];

        if (children.Length == 0)
        {
            return type switch
            {
                // An empty environment value is the only way the deployed form writes an empty array or
                // object, so it is one here, and the schema's minItems or required speaks to it.
                "array" when string.IsNullOrEmpty(section.Value) => new JsonArray(),
                "object" when string.IsNullOrEmpty(section.Value) => new JsonObject(),
                "integer" when long.TryParse(section.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
                    => JsonValue.Create(number),
                _ => JsonValue.Create(section.Value),
            };
        }

        if (type == "array" && Indices(children) is { } ordered)
        {
            JsonElement? items = resolved!.Value.TryGetProperty("items", out JsonElement i) ? i : null;
            return new JsonArray([.. ordered.Select(child => Node(child, items, resolvedFile))]);
        }

        JsonObject node = [];
        foreach (IConfigurationSection child in children)
        {
            (string name, JsonElement? childSchema) = type == "object" ? Child(resolved!.Value, child.Key) : (child.Key, null);
            node[name] = Node(child, childSchema, resolvedFile);
        }

        return node;
    }

    /// <summary>
    /// A child's name and schema: a property the schema names, under the schema's spelling, because
    /// configuration keys are case-insensitive (https://learn.microsoft.com/dotnet/core/extensions/configuration#configuration-keys-and-values)
    /// and the schema's are not; else <c>additionalProperties</c> when that is a schema, under the
    /// configuration's spelling (an issuer's name); else no schema at all.
    /// </summary>
    private static (string Name, JsonElement? Schema) Child(JsonElement schema, string key)
    {
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return (property.Name, property.Value);
                }
            }
        }

        return schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.Object
            ? (key, additional)
            : (key, null);
    }

    /// <summary>The children in index order when every key is an array index, as the binder reads an array; else null.</summary>
    private static IConfigurationSection[]? Indices(IConfigurationSection[] children)
    {
        List<(int Index, IConfigurationSection Child)> indexed = [];
        foreach (IConfigurationSection child in children)
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                return null;
            }

            indexed.Add((index, child));
        }

        return [.. indexed.OrderBy(pair => pair.Index).Select(pair => pair.Child)];
    }

    /// <summary>Follows <c>$ref</c>s to the subschema that states the type.</summary>
    private static (JsonElement? Schema, string File) Resolve(JsonElement schema, string file)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (schema.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("$ref", out JsonElement reference)
                || reference.GetString() is not { } target)
            {
                return (schema, file);
            }

            (schema, file) = At(target.StartsWith('#') ? file + target : target);
        }

        throw new InvalidOperationException($"contracts/{file} has a $ref chain longer than 16.");
    }

    /// <summary>
    /// The subschema an evaluation path reaches from the root schema, following each <c>$ref</c> segment
    /// where it stands (https://json-schema.org/draft/2020-12/json-schema-core#section-12.3.1). The
    /// engine's absolute schema location is not used: for a subschema under <c>additionalProperties</c>
    /// JsonSchema.Net 8.0.5 reports one with the instance's property name appended
    /// (<c>#/properties/Issuers/additionalProperties/Issuers</c>), which names no subschema.
    /// </summary>
    private static (JsonElement Schema, string File) Along(string evaluationPath)
    {
        JsonElement node = Schemas[SchemaFile];
        string file = SchemaFile;
        foreach (string segment in Segments(evaluationPath))
        {
            if (segment == "$ref")
            {
                string target = node.GetProperty("$ref").GetString()!;
                (node, file) = At(target.StartsWith('#') ? file + target : target);
            }
            else
            {
                node = node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out JsonElement next)
                    ? next
                    : throw new InvalidOperationException($"The evaluation path {evaluationPath} names no subschema of contracts/{file}.");
            }
        }

        return (node, file);
    }

    /// <summary>
    /// The subschema at an absolute or file-relative location such as
    /// <c>https://spa-platform.internal/contracts/registry.schema.json#/$defs/appId</c>.
    /// </summary>
    private static (JsonElement Schema, string File) At(string location)
    {
        string relative = location.StartsWith(RegistrySchemas.SchemaIdBase, StringComparison.Ordinal)
            ? location[RegistrySchemas.SchemaIdBase.Length..]
            : location;
        int hash = relative.IndexOf('#', StringComparison.Ordinal);
        string file = hash < 0 ? relative : relative[..hash];
        string pointer = hash < 0 ? "" : Uri.UnescapeDataString(relative[(hash + 1)..]);
        JsonElement node = Schemas.TryGetValue(file, out JsonElement root)
            ? root
            : throw new InvalidOperationException($"{location} names no contract schema.");
        foreach (string segment in Segments(pointer))
        {
            node = node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out JsonElement next)
                ? next
                : throw new InvalidOperationException($"{location} names no subschema.");
        }

        return (node, file);
    }

    /// <summary>
    /// The keys a failing <c>required</c> asked for that the instance at <paramref name="location"/>
    /// lacks, read from the subschema's own <c>required</c> rather than from the engine's message. A
    /// missing section is named by the keys it requires in turn, because the deployed form sets keys,
    /// never sections: with no <c>Platform__Cell</c> the whole <c>Platform</c> section is absent, and the
    /// key to set is <c>Platform:Cell</c>. A section that requires nothing is named itself.
    /// </summary>
    private static List<string[]> Missing(string evaluationPath, JsonElement instance, IReadOnlyList<string> location)
    {
        (JsonElement schema, string file) = Along(evaluationPath);
        JsonElement node = instance;
        foreach (string segment in location)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
            {
                return [];
            }
        }

        if (node.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("required", out JsonElement required))
        {
            return [];
        }

        List<string[]> missing = [];
        foreach (JsonElement name in required.EnumerateArray())
        {
            if (name.GetString() is { } text && !node.TryGetProperty(text, out _))
            {
                (string _, JsonElement? property) = Child(schema, text);
                missing.AddRange(RequiredLeaves(property, file).Select(leaf => (string[])[text, .. leaf]));
            }
        }

        return missing;
    }

    /// <summary>The keys an absent value of <paramref name="schema"/> leaves unset: its own required keys, recursively, or itself.</summary>
    private static IEnumerable<string[]> RequiredLeaves(JsonElement? schema, string file)
    {
        (JsonElement? resolved, string resolvedFile) = schema is { } given ? Resolve(given, file) : (null, file);
        if (resolved is not { ValueKind: JsonValueKind.Object } subschema
            || !subschema.TryGetProperty("required", out JsonElement required)
            || required.GetArrayLength() == 0)
        {
            yield return [];
            yield break;
        }

        foreach (JsonElement name in required.EnumerateArray())
        {
            string text = name.GetString()!;
            (string _, JsonElement? property) = Child(subschema, text);
            foreach (string[] leaf in RequiredLeaves(property, resolvedFile))
            {
                yield return [text, .. leaf];
            }
        }
    }

    /// <summary>A JSON pointer's reference tokens, unescaped (https://www.rfc-editor.org/rfc/rfc6901#section-4).</summary>
    private static string[] Segments(string pointer) =>
        [.. pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(token => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];

    private static Dictionary<string, JsonElement> LoadSchemas()
    {
        Dictionary<string, JsonElement> schemas = new(StringComparer.Ordinal);
        foreach (string file in new[] { RegistrySchemas.Registry, RegistrySchemas.AppDefinition, RegistrySchemas.PlatformConfiguration, RegistrySchemas.InfraOutputs })
        {
            using JsonDocument document = JsonDocument.Parse(RegistrySchemas.SchemaText(file));
            schemas.Add(file, document.RootElement.Clone());
        }

        return schemas;
    }
}
