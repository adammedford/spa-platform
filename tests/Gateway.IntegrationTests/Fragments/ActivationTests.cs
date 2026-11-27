using System.Net;
using System.Text;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// F-29, O6 (#1007, part of #961 -> "A fragment's release lifecycle"): <c>spactl fragment activate</c>
/// moves one fragment between releases on a running gateway, which serves the new release's bytes under
/// <c>/fragments/{fragmentId}/</c> to the next request with no restart, while the shell's release and its
/// <c>index.html</c> stay as they were. There is no one-release-per-document guarantee: a chunk the old
/// release had and the new one lacks answers 404 <c>private, no-store</c>, and a reload of the document is
/// the recovery.
/// </summary>
public sealed class ActivationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ReleaseB = "2026-11-02.1";

    [Fact]
    public async Task G9_O6_fragment_activation_A_B_A_serves_new_bytes_with_no_restart_and_leaves_the_shell_alone()
    {
        // Mutation that turns it red: a hosting route that pins a fragment's release when the app's entry
        // was first read (rather than from each request's snapshot) keeps serving A's entry after the
        // activation to B, so the first B assertion fails. Mutation: an activation that also writes the
        // shell's release changes the shell's index.html bytes or release, so the shell assertions fail.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        HarnessApp shell = harness.App("shell");
        string releaseA = harness.Options.Release;
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await Fragments.LoginAsync(browser, shell, "/", Ct)).StatusCode);

        // A, as the harness published it: the fixture's entry and its panel chunk.
        IReadOnlyDictionary<string, byte[]> a = FixtureFiles.Fragment("shell", "orders");
        const string ChunkA = "assets/panel.d2ff9911.js";
        Assert.Contains(ChunkA, a.Keys);

        // B: another entry and another chunk, A's chunk left out.
        const string ChunkB = "assets/panel.b0b0b0b0.js";
        const string EntryB = "export const release = 'B';\nexport { panel } from './assets/panel.b0b0b0b0.js';\n";
        (int published, _, string publishError) = await SpactlRuns.RunAsync(harness, Ct,
            "fragment", "publish", "shell", "orders",
            SpactlRuns.Directory(SpactlRuns.Files(("entry.js", EntryB), (ChunkB, "export const panel = 'B';\n"))), ReleaseB);
        Assert.True(published == 0, publishError);

        string shellIndex = await TextAsync(browser, shell, "/");
        string shellRelease = Release(harness);
        Assert.Equal(Encoding.UTF8.GetString(a["entry.js"]), await TextAsync(browser, shell, "/fragments/orders/entry.js"));

        // A -> B.
        await ActivateAsync(harness, ReleaseB);
        Assert.Equal(EntryB, await TextAsync(browser, shell, "/fragments/orders/entry.js"));
        Assert.Equal("export const panel = 'B';\n", await TextAsync(browser, shell, "/fragments/orders/" + ChunkB));

        // A lazy chunk of A that a document loaded under A could still ask for: B lacks it.
        BrowserResponse gone = await browser.GetAsync(shell.Url("/fragments/orders/" + ChunkA).ToString(), ct: Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        // Compared as directives: the client's header parser writes them back in its own order.
        Assert.Equal(Directives(Contract.AssetNotFoundCacheControl), Directives(gone.Header("Cache-Control")));

        // The recovery: a reload of the document, after which B's entry and its chunk load.
        Assert.Equal(HttpStatusCode.OK, (await browser.NavigateAsync(shell.Url("/"), ct: Ct)).StatusCode);
        Assert.Equal(EntryB, await TextAsync(browser, shell, "/fragments/orders/entry.js"));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(shell.Url("/fragments/orders/" + ChunkB).ToString(), ct: Ct)).StatusCode);

        // The shell is as it was: its index.html bytes and its release.
        Assert.Equal(shellIndex, await TextAsync(browser, shell, "/"));
        Assert.Equal(shellRelease, Release(harness));

        // B -> A: the rollback is the same command to the earlier release (never registry rollback).
        await ActivateAsync(harness, releaseA);
        Assert.Equal(Encoding.UTF8.GetString(a["entry.js"]), await TextAsync(browser, shell, "/fragments/orders/entry.js"));
        Assert.Equal(Encoding.UTF8.GetString(a[ChunkA]), await TextAsync(browser, shell, "/fragments/orders/" + ChunkA));
        Assert.Equal(shellIndex, await TextAsync(browser, shell, "/"));
        Assert.Equal(shellRelease, Release(harness));

        // No restart: the harness started one host and starts no other in this test, so every step above
        // was served by that running host. What a restart would lose, the session signed in before A -> B,
        // still answers.
        Assert.Equal("signedIn", Fragments.States(await Fragments.SessionAsync(browser, shell, Ct))["orders"]);
    }

    private static async Task ActivateAsync(GatewayHarness harness, string release)
    {
        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct, "fragment", "activate", "shell", "orders", release);
        Assert.True(exit == 0, error);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(release, harness.Registry.Current.ByAppId["shell"].Fragments.Single().Release);
    }

    private static string[] Directives(string? cacheControl) =>
        [.. (cacheControl ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    private static string Release(GatewayHarness harness) =>
        harness.RegistryDocument["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == "shell")!["release"]!.GetValue<string>();

    private static async Task<string> TextAsync(TestBrowser browser, HarnessApp shell, string path)
    {
        BrowserResponse response = path == "/"
            ? await browser.NavigateAsync(shell.Url(path), ct: Ct)
            : await browser.GetAsync(shell.Url(path).ToString(), ct: Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {response}");
        return response.Body;
    }
}
