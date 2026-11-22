using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// The fixture shell and its fragments (#998, part of #961), read as files: the shell's definition
/// under <c>fixtures/fragments/</c>, each fragment's release under
/// <c>fixtures/fragments/{appId}/{fragmentId}/</c> as <c>spactl fragment publish</c> uploads it, and the
/// shell's release under <c>fixtures/spa/{appId}/</c> as <c>release publish</c> does. Expected values
/// are read from the contract schemas and the definition, never restated. That the shell then
/// imports each entry in a browser is <see cref="ShellPageTests"/>.
/// </summary>
public sealed partial class FragmentFixtureTests
{
    /// <summary>O1's run measures redirects at N = 0 to 6 (#961, revision 2 of its ticket graph).</summary>
    private const int FragmentCount = 6;

    [Fact]
    public void There_is_one_fixture_shell_and_it_is_kept_out_of_the_default_definitions()
    {
        // Mutation: moving fixtures/fragments/shell.json into fixtures/apps/ turns this red: the
        // fragment-bearing list is empty, and a default definition then has fragments.
        Assert.Equal(["shell.json"], FixtureFiles.FragmentDefinitions.Select(Path.GetFileName));

        foreach (string path in FixtureFiles.AppDefinitions.Concat(FixtureFiles.OnboardingDefinitions))
        {
            Assert.Null(FixtureFiles.Read(path)["fragments"]);
        }

        foreach (string path in FixtureFiles.FragmentDefinitions)
        {
            Assert.NotEmpty(FixtureFiles.Fragments(FixtureFiles.Read(path)));
        }
    }

    [Fact]
    public void The_shell_lists_six_fragments_one_with_an_entry_other_than_the_default()
    {
        // E2: a non-default entry, so the seed's --entry and the activation's entry check run with a
        // value that is not entry.js. Mutation: removing reports' "entry" turns this red.
        List<JsonNode> fragments = [.. FixtureFiles.Fragments(Shell)];

        Assert.Equal(FragmentCount, fragments.Count);
        Assert.Contains(fragments, fragment => fragment["entry"] is null);
        Assert.Contains(fragments, fragment => fragment["entry"] is { } entry && entry.GetValue<string>() != DefaultEntry);
    }

    [Fact]
    public void Each_fragment_has_a_release_directory_and_nothing_else_is_there()
    {
        // The directories the seed publishes are the definition's, and only those. Mutation: renaming
        // one fragment's directory turns this red.
        Assert.Equal(
            FixtureFiles.Fragments(Shell).Select(IdOf).Order(StringComparer.Ordinal),
            Directory.EnumerateDirectories(FragmentsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryFragment))]
    public void Each_release_has_its_entry_at_the_root_and_a_relative_chunk_under_assets(string fragmentId)
    {
        // The entry module at the definition's entry (default entry.js), importing at least one
        // chunk by a relative URL under assets/, so it resolves under /fragments/{fragmentId}/assets/
        // (O12). Mutation: importing the chunk by an absolute path turns this red.
        string root = Path.Combine(FragmentsRoot, fragmentId);
        string entry = Path.Combine(root, EntryOf(fragmentId));

        Assert.True(File.Exists(entry), $"{fragmentId} has no {EntryOf(fragmentId)} at its release root.");
        List<string> imports = [.. RelativeImport().Matches(File.ReadAllText(entry)).Select(match => match.Groups["path"].Value)];
        Assert.NotEmpty(imports);
        foreach (string chunk in imports)
        {
            Assert.True(File.Exists(Path.Combine(root, chunk)), $"{fragmentId}'s entry imports ./{chunk}, which its release lacks.");
        }
    }

    [Theory]
    [MemberData(nameof(EveryFragment))]
    public void Each_release_has_no_top_level_api_and_its_entry_follows_the_grammar(string fragmentId)
    {
        // "spactl fragment publish refuses a release with a top-level api/", and the entry name is the
        // registry's entry rule. Mutation: adding api/x.json to one release turns this red.
        string root = Path.Combine(FragmentsRoot, fragmentId);
        JsonObject rule = Rules["entry"]!.AsObject();

        Assert.False(Directory.Exists(Path.Combine(root, "api")), $"{fragmentId} has a top-level api/.");
        Assert.Matches(rule["pattern"]!.GetValue<string>(), EntryOf(fragmentId));
        Assert.True(EntryOf(fragmentId).Length <= rule["maxLength"]!.GetValue<int>());
    }

    [Theory]
    [MemberData(nameof(EveryFragment))]
    public void Each_asset_is_named_for_its_bytes(string fragmentId)
    {
        // The asset invariant: within a fragment's namespace an assets/ URL always identifies the same
        // bytes, which the publish's write-once claim holds. With no build step the name is set by
        // hand, so the test recomputes it. Mutation: editing a chunk without renaming it turns this red.
        AssertNamedForBytes(Path.Combine(FragmentsRoot, fragmentId));
    }

    [Fact]
    public void Each_fragment_meets_the_shells_rules()
    {
        // FragmentIssuerMismatch: a fragment's issuer is the shell's. Its oauth names no client, as
        // the shell's does not (the apps root and spactl app add fill them). And at least one fragment
        // API names an upstream the shell's own apis also name, so one upstream is reached with the
        // fragment's token on its route and the shell's on the shell's (O11). Mutation: pointing every
        // fragment's API at reports turns the last assertion red.
        string issuer = Shell["oauth"]!["issuer"]!.GetValue<string>();
        HashSet<string> shellUpstreams = [.. Shell["apis"]!.AsArray().Select(api => api!["upstream"]!.GetValue<string>())];

        foreach (JsonNode fragment in FixtureFiles.Fragments(Shell))
        {
            Assert.Equal(issuer, fragment["oauth"]!["issuer"]!.GetValue<string>());
            Assert.Null(fragment["oauth"]!["clientId"]);
            Assert.Null(fragment["oauth"]!["clientSecretRef"]);
            Assert.Null(fragment["release"]);
        }

        Assert.Contains(
            FixtureFiles.Fragments(Shell).SelectMany(fragment => fragment["apis"]?.AsArray() ?? []),
            api => shellUpstreams.Contains(api!["upstream"]!.GetValue<string>()));
    }

    [Fact]
    public void The_shell_document_has_one_strict_form_import_map_whose_hash_its_policy_lists()
    {
        // "One static import map in strict form (start tag exactly <script type="importmap">, ASCII,
        // LF line ends)", and its SHA-256 in the definition's csp.script-src, which is what the
        // registry write check asks of the merged script-src. Mutation: changing one byte of the map
        // without its hash turns this red, as do a second map, a CR and a non-ASCII byte.
        byte[] document = File.ReadAllBytes(Path.Combine(ShellRelease, "index.html"));
        string html = Encoding.ASCII.GetString(document);

        Assert.All(document, value => Assert.True(value < 0x80, "index.html is not ASCII."));
        Assert.DoesNotContain("\r", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "importmap", RegexOptions.IgnoreCase, RegexTimeout.Value));

        string map = MapText(html);
        string source = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(map))) + "'";
        Assert.Contains(source, Shell["csp"]!["script-src"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void The_map_reader_refuses_a_document_without_a_strict_form_start_tag()
    {
        // The test above passes through MapText; this feeds it a map in a form the write check would
        // not read. Mutation: matching the start tag case-insensitively turns this red.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MapText("<script TYPE=\"importmap\">{}</script>"));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MapText("<script type='importmap'>{}</script>"));
    }

    [Fact]
    public void The_shell_release_loads_the_reference_client_byte_for_byte()
    {
        // The release is uploaded as its directory, so the shell carries a copy of client/bff-client.js
        // at its root. Mutation: editing the client without copying it turns this red.
        Assert.Equal(
            File.ReadAllBytes(FixtureFiles.Path("client", "bff-client.js")),
            File.ReadAllBytes(Path.Combine(ShellRelease, "bff-client.js")));
        Assert.Contains("<script src=\"/bff-client.js\"></script>", File.ReadAllText(Path.Combine(ShellRelease, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shells_assets_are_named_for_their_bytes()
    {
        // Mutation: editing the shared module without renaming it turns this red.
        AssertNamedForBytes(ShellRelease);
    }

    public static TheoryData<string> EveryFragment => [.. FixtureFiles.Fragments(Shell).Select(IdOf)];

    private static JsonNode Shell => FixtureFiles.Definition("shell");

    private static string AppId => Shell["appId"]!.GetValue<string>();

    private static string FragmentsRoot => FixtureFiles.Path("fixtures", "fragments", AppId);

    private static string ShellRelease => FixtureFiles.Path("fixtures", "spa", AppId);

    private static JsonNode Rules => FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"))["$defs"]!;

    private static string DefaultEntry => Rules["entry"]!["default"]!.GetValue<string>();

    private static string IdOf(JsonNode fragment) => fragment["fragmentId"]!.GetValue<string>();

    private static string EntryOf(string fragmentId) =>
        FixtureFiles.Fragments(Shell).Single(fragment => IdOf(fragment) == fragmentId)["entry"]?.GetValue<string>() ?? DefaultEntry;

    /// <summary>
    /// The import map's exact text: what lies between the strict-form start tag and the next end tag,
    /// as the registry write check extracts it (#961, "The write check").
    /// </summary>
    private static string MapText(string html)
    {
        const string start = "<script type=\"importmap\">";
        int at = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "the document has no strict-form import map.");
        int from = at + start.Length;
        int end = html.IndexOf("</script>", from, StringComparison.Ordinal);
        Assert.True(end >= 0, "the import map is not closed.");
        return html[from..end];
    }

    private static void AssertNamedForBytes(string root)
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "assets")))
        {
            Match name = HashedName().Match(Path.GetFileName(file));
            Assert.True(name.Success, $"{file} is not named <name>.<8 hex digits>.js.");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))[..8], name.Groups["hash"].Value);
        }
    }

    [GeneratedRegex("""\bfrom\s+"\./(?<path>assets/[^"]+)";""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RelativeImport();

    [GeneratedRegex("^[a-z]+\\.(?<hash>[0-9a-f]{8})\\.js$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex HashedName();
}
