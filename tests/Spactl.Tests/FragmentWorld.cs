using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Spactl.Tests;

/// <summary>
/// What the fragment and import-map tests share: the contract's app definition with its one fragment
/// (contracts/app-definition.example.json), the infrastructure outputs that carry that fragment's
/// client, release directories built in the test's scratch directory, and the hash source a shell's
/// import map needs. Every value is read from the contract examples, never repeated here.
/// </summary>
internal sealed class FragmentWorld : IDisposable
{
    private int files;

    public Harness Spactl { get; } = new();

    /// <summary>contracts/app-definition.example.json: app1 as a shell with the fragment <see cref="FragmentId"/>.</summary>
    public static JsonObject DefinitionJson => Harness.ExampleJson("app-definition.example.json");

    public static string AppId => DefinitionJson["appId"]!.GetValue<string>();

    public static string FragmentId => DefinitionJson["fragments"]![0]!["fragmentId"]!.GetValue<string>();

    public static string FragmentNamespace => $"apps/{AppId}/fragments/{FragmentId}/";

    public void Dispose() => Spactl.Dispose();

    /// <summary>The example definition, changed, written to a file of its own.</summary>
    public string Definition(Action<JsonObject>? change = null)
    {
        JsonObject definition = DefinitionJson;
        change?.Invoke(definition);
        return Spactl.WriteFile($"definition-{++files}.json", definition.ToJsonString());
    }

    /// <summary>Sets the shell's own <c>csp.script-src</c> of a definition, or removes it when empty.</summary>
    public static void ScriptSrc(JsonObject definition, params string[] sources)
    {
        JsonObject csp = definition["csp"] as JsonObject ?? [];
        if (sources.Length == 0)
        {
            csp.Remove("script-src");
        }
        else
        {
            csp["script-src"] = new JsonArray([.. sources.Select(source => (JsonNode)source)]);
        }

        definition["csp"] = csp;
    }

    /// <summary>A shell's <c>index.html</c>, with one import map holding <paramref name="map"/> when it is given.</summary>
    public static string ShellIndex(string? map) =>
        "<!doctype html>\n<html><head>\n"
        + (map is null ? "" : ImportMapTag(map) + "\n")
        + "<script type=\"module\" src=\"/assets/app.js\"></script>\n</head><body></body></html>\n";

    public static string ImportMapTag(string map) => "<script type=\"importmap\">" + map + "</script>";

    /// <summary>A distinct import map per name, so two releases have two hashes.</summary>
    public static string Map(string name) => "{\"imports\":{\"lib\":\"/assets/" + name + ".js\"}}";

    /// <summary>
    /// The CSP hash source of a map's text (https://w3c.github.io/webappsec-csp/#grammardef-hash-source),
    /// computed here from the text, not by the code under test.
    /// </summary>
    public static string HashSource(string text, string algorithm = "sha256")
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        byte[] digest = algorithm switch
        {
            "sha256" => SHA256.HashData(bytes),
            "sha384" => SHA384.HashData(bytes),
            "sha512" => SHA512.HashData(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return $"'{algorithm}-{Convert.ToBase64String(digest)}'";
    }

    /// <summary>A release directory holding exactly <paramref name="contents"/>, by relative path.</summary>
    public string Directory(string name, IReadOnlyDictionary<string, string> contents)
    {
        string root = Spactl.ScratchDirectory($"dir-{++files}-{name}");
        foreach ((string path, string text) in contents)
        {
            string file = Path.Combine(root, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        return root;
    }

    /// <summary>Publishes a shell release of app1: its <c>index.html</c> and whatever else is named.</summary>
    public Task<int> PublishShell(string release, string? map = null, IReadOnlyDictionary<string, string>? more = null) =>
        Spactl.Run("release", "publish", AppId, Directory("shell", WithFile(more, "index.html", ShellIndex(map))), release);

    /// <summary>Publishes a release of the example's fragment: its entry file and whatever else is named.</summary>
    public Task<int> PublishFragment(string release, string entry = PlatformPaths.DefaultEntry, IReadOnlyDictionary<string, string>? more = null) =>
        Spactl.Run("fragment", "publish", AppId, FragmentId, Directory("fragment", WithFile(more, entry, "export const entry = true;\n")), release, "--entry", entry);

    /// <summary>
    /// Writes a registry document to the bucket directly, over the live one, as an operator with
    /// bucket access could: the way a test makes a live entry no spactl write would have accepted.
    /// </summary>
    public async Task OverwriteRegistry(JsonObject document)
    {
        StoredObject? live = await Spactl.Store.GetAsync(Harness.RegistryKey, null, CancellationToken.None);
        live?.Body?.Dispose();
        Assert.True(await Spactl.Store.PutAsync(
            Harness.RegistryKey, new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString())), live?.ETag, CancellationToken.None));
    }

    /// <summary>Publishes a shell release, a fragment release and adds the example definition at both.</summary>
    public async Task Onboard(string shellRelease = "r1", string fragmentRelease = "f1", string? map = null, Action<JsonObject>? change = null)
    {
        Assert.Equal(0, await PublishShell(shellRelease, map));
        Assert.Equal(0, await PublishFragment(fragmentRelease));
        Assert.Equal(0, await Spactl.Run(
            "app", "add", Definition(change), "--release", shellRelease, "--fragment-release", $"{FragmentId}={fragmentRelease}"));
    }

    private static Dictionary<string, string> WithFile(IReadOnlyDictionary<string, string>? more, string path, string text)
    {
        Dictionary<string, string> contents = more is null ? [] : new(more);
        contents.TryAdd(path, text);
        return contents;
    }
}
