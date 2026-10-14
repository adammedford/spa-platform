using System.Text.Json;
using System.Text.Json.Nodes;

namespace Platform.Registry;

/// <summary>
/// Reads a JSON Schema <c>integer</c> as the platform's <see cref="long"/>. Draft 2020-12 calls any
/// number with a zero fractional part an integer, so <c>30</c>, <c>30.0</c> and <c>3e1</c> all pass
/// <c>"type": "integer"</c> (https://json-schema.org/draft/2020-12/json-schema-core#section-6.3), and
/// the schemas bound no upper value for <c>revision</c> or <c>timeoutSeconds</c>. <see cref="JsonElement.TryGetInt64"/>
/// accepts only the first spelling and only within Int64
/// (https://learn.microsoft.com/dotnet/api/system.text.json.jsonelement.trygetint64), so a reader that
/// trusted the schema threw on a schema-valid document instead of reporting it.
/// </summary>
internal static class JsonInteger
{
    /// <summary>The value, when <paramref name="node"/> is an integral number that fits a <see cref="long"/>.</summary>
    public static bool TryRead(JsonNode node, out long value)
    {
        value = 0;
        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        // A parsed document's values are JsonElement-backed; a node built in code holds a CLR value.
        JsonElement element = node is JsonValue parsed && parsed.TryGetValue(out JsonElement backing)
            ? backing
            : JsonElement.Parse(node.ToJsonString());
        if (element.TryGetInt64(out value))
        {
            return true;
        }

        // 30.0 or 3e1: decimal keeps every integer in Int64's range exactly, where double would not.
        if (element.TryGetDecimal(out decimal number) && number == decimal.Truncate(number)
            && number >= long.MinValue && number <= long.MaxValue)
        {
            value = (long)number;
            return true;
        }

        return false;
    }

    /// <summary>For a value the validator has already accepted through <see cref="TryRead"/>.</summary>
    public static long Read(JsonNode node) =>
        TryRead(node, out long value) ? value : throw new InvalidOperationException("An accepted integer could not be read.");
}
