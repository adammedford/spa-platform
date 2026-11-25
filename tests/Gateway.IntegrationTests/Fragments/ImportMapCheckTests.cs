using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// N-42 (#1007, part of #961 -> "The import map"): every registry write proves the shell's import map
/// against the entry's <c>script-src</c>. A write naming a shell release whose map's hash the entry does
/// not list, or whose <c>index.html</c> holds a second map, is refused naming the missing
/// <c>'sha256-…'</c> source or the second map, and the live document stays, in the bucket and on the
/// gateway.
/// </summary>
public sealed class ImportMapCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string StartTag = "<script type=\"importmap\">";

    [Fact]
    public async Task G9_the_write_check_refuses_a_missing_hash_and_a_second_map()
    {
        // Mutation that turns it red: a write check that proves the map only on release activate (not on
        // registry apply), or that reads the first map and stops, lets one of the writes through, and the
        // live revision moves.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        IReadOnlyDictionary<string, byte[]> fixture = FixtureFiles.Spa("shell");
        string index = Encoding.UTF8.GetString(fixture["index.html"]);
        Assert.Contains(StartTag, index, StringComparison.Ordinal);

        // A map the entry's script-src does not list: the same map naming another chunk.
        string otherMap = index.Replace("ui.748e4831.js", "ui.0000beef.js", StringComparison.Ordinal);
        string missing = HashSource(MapOf(otherMap));
        // Read from the entry's parsed script-src, not the serialized document, which escapes the quotes
        // and the plus a hash source holds and so could never contain it.
        string[] listed = [.. harness.RegistryDocument["apps"]![0]!["csp"]!["script-src"]!.AsArray().Select(source => source!.GetValue<string>())];
        Assert.NotEmpty(listed);
        Assert.DoesNotContain(missing, listed);

        // A second map after the first.
        string twoMaps = index.Replace("</head>", StartTag + "{\"imports\":{}}</script>\n</head>", StringComparison.Ordinal);

        foreach ((string release, string html, string named) in new[]
        {
            ("2026-11-04.1", otherMap, missing),
            ("2026-11-04.2", twoMaps, "more than one import map"),
        })
        {
            Dictionary<string, byte[]> files = new(fixture, StringComparer.Ordinal) { ["index.html"] = Encoding.UTF8.GetBytes(html) };
            (int published, _, string publishError) = await SpactlRuns.RunAsync(harness, Ct,
                "release", "publish", "shell", SpactlRuns.Directory(files), release);
            Assert.True(published == 0, publishError);

            string? before = await SpactlRuns.TextAsync(harness, GatewayHarness.RegistryKey, Ct);
            long live = harness.Registry.Current.Revision;
            JsonObject next = harness.RegistryDocument;
            next["revision"] = live + 1;
            next["apps"]![0]!["release"] = release;

            (int applied, _, string applyError) = await SpactlRuns.RunAsync(harness, Ct, "registry", "apply", SpactlRuns.Document(next));
            (int activated, _, string activateError) = await SpactlRuns.RunAsync(harness, Ct, "release", "activate", "shell", release);

            Assert.Equal(1, applied);
            Assert.Contains(named, applyError, StringComparison.Ordinal);
            Assert.Equal(1, activated);
            Assert.Contains(named, activateError, StringComparison.Ordinal);
            Assert.Equal(before, await SpactlRuns.TextAsync(harness, GatewayHarness.RegistryKey, Ct));
            Assert.Equal(RefreshOutcome.NotModified, await harness.RefreshRegistryAsync(Ct));
            Assert.Equal(live, harness.Registry.Current.Revision);
            Assert.Equal(harness.Options.Release, harness.Registry.Current.ByAppId["shell"].Release);
        }
    }

    /// <summary>The map's text: what lies between its start tag and the next end tag.</summary>
    private static string MapOf(string html)
    {
        int start = html.IndexOf(StartTag, StringComparison.Ordinal) + StartTag.Length;
        return html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
    }

    /// <summary>The CSP hash source of a map's text, computed here from the text, not by the code under test.</summary>
    private static string HashSource(string map) =>
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(map.Replace("\r\n", "\n", StringComparison.Ordinal)))) + "'";
}
