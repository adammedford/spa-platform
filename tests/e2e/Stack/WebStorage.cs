using System.Text.Json;

namespace Gateway.E2ETests.Stack;

/// <summary>F-05's check of web storage as the page's own script sees it: the browser keeps nothing there.</summary>
public static class WebStorage
{
    /// <summary>The page script F-05 runs: both storages, as one JSON object of their entries.</summary>
    public const string Script = "() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } })";

    /// <summary>What <see cref="Script"/> returns when both storages are empty.</summary>
    public const string Empty = """{"local":{},"session":{}}""";

    /// <summary>
    /// Fails when <paramref name="storage"/>, what <see cref="Script"/> returned, is not <see cref="Empty"/>,
    /// counting the entries in each storage but naming none: an entry is what F-05 exists to catch, on the
    /// cell it may be a real token, and a failure message is output too (AGENTS.md). xUnit's own Equal
    /// would print the storage.
    /// </summary>
    public static void AssertEmpty(string storage)
    {
        if (storage == Empty)
        {
            return;
        }

        Assert.Fail($"web storage is not empty: {Count(storage, "local")} entries in localStorage, {Count(storage, "session")} in sessionStorage.");
    }

    private static string Count(string storage, string area)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(storage);
            return document.RootElement.TryGetProperty(area, out JsonElement entries) && entries.ValueKind == JsonValueKind.Object
                ? entries.EnumerateObject().Count().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "an unknown number of";
        }
        catch (JsonException)
        {
            return "an unknown number of";
        }
    }
}
