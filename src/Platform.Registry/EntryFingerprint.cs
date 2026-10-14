using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Platform.Registry;

/// <summary>
/// The per-entry fingerprint: a hash of the entry's canonical JSON (docs/plan/02-contracts.md,
/// Registry document; GLOSSARY.md, Fingerprint). It decides eviction, so it must depend on what the
/// entry says and on nothing about how the document was written.
///
/// Canonical here means: object members sorted by name (ordinal), array order kept (the order of
/// <c>scopes</c> or <c>apis</c> is the author's), no insignificant whitespace, and one fixed string
/// escaping. Entries carry only strings and integers, so number formatting is not a question.
/// </summary>
public static class EntryFingerprint
{
    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    /// <summary><c>sha256:</c> and the lowercase hex digest of the entry's canonical JSON.</summary>
    public static string Of(JsonNode entry)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Compact))
        {
            WriteCanonical(writer, entry);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> member in value.OrderBy(member => member.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Key);
                    WriteCanonical(writer, member.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (JsonNode? item in value)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                // A scalar is written from its parsed value, so "a" and "a" agree.
                node.AsValue().WriteTo(writer);
                break;
        }
    }
}
