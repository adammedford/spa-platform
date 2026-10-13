using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// The schemas and the code have to agree about what is draft.
///
/// contracts/README.md says "every draft subschema carries an x-freeze annotation naming the gate,
/// so the marker travels with the schema rather than only with this page", and
/// <see cref="FreezeAttribute"/> is that idea for the code. Two halves of one rule, and until this
/// file existed only the schema half was derived from the artifact: Contracts.Tests walks the
/// schema tree for every draft subschema, while the code half was a hand-typed list. So the halves
/// drifted. <see cref="OAuthDefinition"/> is the C# form of a subschema marked draft in two
/// schemas and carried no marker, and it passed CI, a review and a retrospective that way.
///
/// This walks the schemas and holds the code to what they say. <see cref="PublicSurfaceTests"/>
/// checks the other direction, that the marked set is the one the contract names.
/// </summary>
public sealed class FreezeMarkersTests
{
    /// <summary>
    /// The code form of each subschema marked draft, as <c>file#json-pointer</c>. The mapping is
    /// written out because nothing in a schema names a C# type, and a test below fails on a draft
    /// subschema missing from here: adding one is then a decision about its code counterpart
    /// rather than something that passes in silence.
    ///
    /// Two of the three are the same type, because a registry entry and an app definition share
    /// the subschema by <c>$ref</c>. A null means the draft part has no counterpart in
    /// <c>Platform.Abstractions</c> at all.
    /// </summary>
    private static readonly Dictionary<string, string?> CodeForm = new()
    {
        ["app-definition.schema.json#/properties/oauth"] = nameof(OAuthDefinition),
        ["registry.schema.json#/$defs/oauth"] = nameof(OAuthDefinition),
        ["platform-config.schema.json#/properties/Issuers/additionalProperties/properties/Profile"] =
            nameof(IIdpProfile),
    };

    [Fact]
    public void Every_draft_subschema_names_its_code_form()
    {
        Assert.Equal(CodeForm.Keys.Order(), DraftSubschemas().Order());
    }

    [Fact]
    public void Every_code_form_of_a_draft_subschema_carries_the_marker()
    {
        string[] unmarked =
        [
            .. CodeForm
                .Where(entry => entry.Value is not null)
                .Select(entry => entry.Value!)
                .Distinct()
                .Where(name => Marker(name) is not { Gate: "G1b" })
                .Order(),
        ];

        // Compared against an empty array rather than asserted empty, so a failure prints the name
        // of the type whose marker is missing.
        Assert.Equal<string[]>([], unmarked);
    }

    /// <summary>
    /// Every <c>file#json-pointer</c> in <c>contracts/</c> whose <c>x-freeze</c> says draft, found
    /// by walking each schema rather than by being listed.
    /// </summary>
    private static IEnumerable<string> DraftSubschemas()
    {
        foreach (string file in Directory.EnumerateFiles(ContractsDirectory(), "*.schema.json").Order())
        {
            JsonNode? schema = JsonNode.Parse(File.ReadAllText(file));
            foreach (string pointer in DraftPointers(schema, string.Empty))
            {
                yield return $"{Path.GetFileName(file)}#{pointer}";
            }
        }
    }

    private static IEnumerable<string> DraftPointers(JsonNode? node, string pointer)
    {
        switch (node)
        {
            case JsonObject objectNode:
                if (objectNode.TryGetPropertyValue("x-freeze", out JsonNode? freeze)
                    && freeze?["status"]?.GetValue<string>() == "draft")
                {
                    yield return pointer;
                }

                foreach (KeyValuePair<string, JsonNode?> property in objectNode)
                {
                    foreach (string found in DraftPointers(property.Value, $"{pointer}/{property.Key}"))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonArray arrayNode:
                for (int index = 0; index < arrayNode.Count; index++)
                {
                    foreach (string found in DraftPointers(arrayNode[index], $"{pointer}/{index}"))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    private static FreezeAttribute? Marker(string typeName) =>
        typeof(AppDefinition).Assembly.GetExportedTypes()
            .Single(type => type.Name == typeName)
            .GetCustomAttribute<FreezeAttribute>();

    /// <summary>
    /// The source tree first, then the output directory's ancestors, the same two places
    /// Contracts.Tests looks. This file sits at tests/Platform.Abstractions.Tests/.
    /// </summary>
    private static string ContractsDirectory([CallerFilePath] string? thisFile = null)
    {
        if (thisFile is not null)
        {
            string fromSource =
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "contracts"));

            if (Directory.Exists(fromSource))
            {
                return fromSource;
            }
        }

        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return Path.Combine(candidate.FullName, "contracts");
            }
        }

        throw new InvalidOperationException("contracts/ is neither beside the source nor above the output.");
    }
}
