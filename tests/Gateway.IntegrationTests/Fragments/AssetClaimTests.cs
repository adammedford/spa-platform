using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// F-36, O10 (#1007, part of #961): the asset invariant, an <c>assets/</c> URL names the same bytes in its
/// namespace, as F-T1 to F-T4 of #961's traces comment (section 3) state it, run with <c>spactl</c> over the
/// harness's bucket, the object-store double, and read back through the composed gateway where a URL is
/// served. A claim is <c>apps/{appId}[/fragments/{fragmentId}]/claims/{relative}</c>, written create-only
/// before the object. F-T5, the same race on the cell's bucket, is F-37's, in the deployed tier.
/// </summary>
public sealed class AssetClaimTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string A = "export const x = 'A';\n";
    private const string B = "export const x = 'B';\n";
    private const string Fragment = "apps/shell/fragments/orders/";
    private const string Shell = "apps/shell/";

    /// <summary>
    /// F-T1: R1 serves <c>assets/x.js</c> = A, R2 without it is activated, R3 with <c>x.js</c> = B is
    /// refused naming R1's claim and leaves neither its <c>x.js</c> nor its marker (the shell's
    /// <c>index.html</c>, a fragment's entry), and R4 with <c>x.js</c> = A is accepted and serves A; in the
    /// shell's namespace and then in a fragment's.
    /// </summary>
    [Fact]
    public async Task G9_O10_F_T1()
    {
        // Mutation that turns it red: claims keyed by release (or no claim at all) accept R3, so its exit
        // and the absent objects fail in both namespaces.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await Fragments.LoginAsync(browser, harness.App("shell"), "/", Ct)).StatusCode);

        // The shell's namespace.
        Assert.Equal(0, await PublishShellAsync(harness, "2026-11-05.1", A));
        await ActivateShellAsync(harness, "2026-11-05.1");
        Assert.Equal(A, await ServedAsync(browser, harness, "/assets/x.js"));
        Assert.Equal(0, await PublishShellAsync(harness, "2026-11-05.2", x: null));
        await ActivateShellAsync(harness, "2026-11-05.2");
        (int refused, string error) = await PublishShellWithErrorAsync(harness, "2026-11-05.3", B);
        Assert.Equal(1, refused);
        Assert.Contains("claimed by release 2026-11-05.1", error, StringComparison.Ordinal);
        Assert.Null(await SpactlRuns.TextAsync(harness, Shell + "releases/2026-11-05.3/assets/x.js", Ct));
        Assert.Null(await SpactlRuns.TextAsync(harness, Shell + "releases/2026-11-05.3/index.html", Ct));
        Assert.Equal(0, await PublishShellAsync(harness, "2026-11-05.4", A));
        await ActivateShellAsync(harness, "2026-11-05.4");
        Assert.Equal(A, await ServedAsync(browser, harness, "/assets/x.js"));

        // A fragment's namespace.
        Assert.Equal(0, (await PublishFragmentAsync(harness, "2026-11-06.1", A)).Exit);
        Assert.Equal(0, (await PublishFragmentAsync(harness, "2026-11-06.2", x: null)).Exit);
        (int fragmentRefused, _, string fragmentError) = await PublishFragmentAsync(harness, "2026-11-06.3", B);
        Assert.Equal(1, fragmentRefused);
        Assert.Contains("claimed by release 2026-11-06.1", fragmentError, StringComparison.Ordinal);
        Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-06.3/assets/x.js", Ct));
        Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-06.3/entry.js", Ct));
        Assert.Equal(0, (await PublishFragmentAsync(harness, "2026-11-06.4", A)).Exit);
        await ActivateFragmentAsync(harness, "2026-11-06.4");
        Assert.Equal(A, await ServedAsync(browser, harness, "/fragments/orders/assets/x.js"));
    }

    /// <summary>
    /// F-T2: two <c>fragment publish</c> runs held at a barrier before the claim; exactly one completes, the
    /// loser leaves no object for the key, and the URL serves the winner's bytes. With the same bytes, both
    /// complete.
    /// </summary>
    [Theory]
    [InlineData(A, B, 1)]
    [InlineData(A, A, 2)]
    public async Task G9_O10_F_T2(string first, string second, int completing)
    {
        // Mutation that turns it red: a claim written unconditionally (not create-only) lets both runs of
        // the first row complete, and the loser's objects stay.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        ClaimBarrier barrier = new(harness.Store, parties: 2);
        Task<(int Exit, string Out, string Error)> one = Task.Run(() => SpactlRuns.RunOverAsync(harness, barrier, Ct,
            "fragment", "publish", "shell", "orders", Directory(first), "2026-11-07.1"), Ct);
        Task<(int Exit, string Out, string Error)> two = Task.Run(() => SpactlRuns.RunOverAsync(harness, barrier, Ct,
            "fragment", "publish", "shell", "orders", Directory(second), "2026-11-07.2"), Ct);
        (int Exit, string Out, string Error)[] runs = await Task.WhenAll(one, two);

        Assert.Equal(2, barrier.Arrived);
        Assert.Equal(completing, runs.Count(run => run.Exit == 0));
        JsonObject claim = JsonNode.Parse((await SpactlRuns.TextAsync(harness, Fragment + "claims/assets/x.js", Ct))!)!.AsObject();
        string winner = claim["release"]!.GetValue<string>();
        string winnerBytes = winner == "2026-11-07.1" ? first : second;
        Assert.Equal(Digest(winnerBytes), claim["sha256"]!.GetValue<string>());
        if (completing == 1)
        {
            string loser = winner == "2026-11-07.1" ? "2026-11-07.2" : "2026-11-07.1";
            Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + $"releases/{loser}/assets/x.js", Ct));
            Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + $"releases/{loser}/entry.js", Ct));
        }

        // The URL serves the winner's bytes once its release is live.
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await Fragments.LoginAsync(browser, harness.App("shell"), "/", Ct)).StatusCode);
        await ActivateFragmentAsync(harness, winner);
        Assert.Equal(winnerBytes, await ServedAsync(browser, harness, "/fragments/orders/assets/x.js"));
    }

    /// <summary>F-T3: a claim exists and its object does not, as a run stopped between them leaves it; a rerun with the same bytes completes.</summary>
    [Fact]
    public async Task G9_O10_F_T3()
    {
        // Mutation that turns it red: a publish that treats any existing claim as a refusal, whatever its
        // digest, exits 1 on the rerun.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        string claim = "{\"sha256\":\"" + Digest(A) + "\",\"release\":\"2026-11-08.1\"}";
        Assert.True(await harness.Store.PutAsync(Fragment + "claims/assets/x.js", new MemoryStream(Encoding.UTF8.GetBytes(claim)), ifMatch: null, Ct));
        Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-08.1/assets/x.js", Ct));

        (int exit, _, string error) = await PublishFragmentAsync(harness, "2026-11-08.1", A);

        Assert.True(exit == 0, error);
        Assert.Equal(A, await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-08.1/assets/x.js", Ct));
        Assert.NotNull(await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-08.1/entry.js", Ct));
    }

    /// <summary>
    /// F-T4: a legacy object, published before claims existed and never claimed, binds nothing: a new release
    /// with other bytes at the same <c>assets/</c> path is accepted, with no backfill (#961, H-F2). The
    /// legacy object is the harness's own release of orders, which it writes as a pre-claims publish did.
    /// </summary>
    [Fact]
    public async Task G9_O10_F_T4()
    {
        // Mutation that turns it red: a publish that backfills claims from the objects already in the
        // namespace refuses the new release, naming the legacy one.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        const string Chunk = "assets/panel.d2ff9911.js";
        string legacy = (await SpactlRuns.TextAsync(harness, Fragment + $"releases/{harness.Options.Release}/{Chunk}", Ct))!;
        Assert.NotEqual(B, legacy);
        Assert.Null(await SpactlRuns.TextAsync(harness, Fragment + "claims/" + Chunk, Ct));

        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct,
            "fragment", "publish", "shell", "orders",
            SpactlRuns.Directory(SpactlRuns.Files(("entry.js", "export {};\n"), (Chunk, B))), "2026-11-09.1");

        Assert.True(exit == 0, error);
        Assert.Equal(B, await SpactlRuns.TextAsync(harness, Fragment + "releases/2026-11-09.1/" + Chunk, Ct));
        JsonObject claim = JsonNode.Parse((await SpactlRuns.TextAsync(harness, Fragment + "claims/" + Chunk, Ct))!)!.AsObject();
        Assert.Equal(Digest(B), claim["sha256"]!.GetValue<string>());
        Assert.Equal(legacy, await SpactlRuns.TextAsync(harness, Fragment + $"releases/{harness.Options.Release}/{Chunk}", Ct));
    }

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A fragment release directory: an entry and, when given, <c>assets/x.js</c>.</summary>
    private static string Directory(string? x) =>
        SpactlRuns.Directory(x is null
            ? SpactlRuns.Files(("entry.js", "export {};\n"))
            : SpactlRuns.Files(("entry.js", "export {};\n"), ("assets/x.js", x)));

    private static Task<(int Exit, string Out, string Error)> PublishFragmentAsync(GatewayHarness harness, string release, string? x) =>
        SpactlRuns.RunAsync(harness, Ct, "fragment", "publish", "shell", "orders", Directory(x), release);

    private static async Task<int> PublishShellAsync(GatewayHarness harness, string release, string? x) =>
        (await PublishShellWithErrorAsync(harness, release, x)).Exit;

    /// <summary>A shell release: the fixture shell, its import map unchanged, and <c>assets/x.js</c> when given.</summary>
    private static async Task<(int Exit, string Error)> PublishShellWithErrorAsync(GatewayHarness harness, string release, string? x)
    {
        Dictionary<string, byte[]> files = new(FixtureFiles.Spa("shell"), StringComparer.Ordinal);
        if (x is not null)
        {
            files["assets/x.js"] = Encoding.UTF8.GetBytes(x);
        }

        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct, "release", "publish", "shell", SpactlRuns.Directory(files), release);
        return (exit, error);
    }

    private static async Task ActivateShellAsync(GatewayHarness harness, string release)
    {
        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct, "release", "activate", "shell", release);
        Assert.True(exit == 0, error);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
    }

    private static async Task ActivateFragmentAsync(GatewayHarness harness, string release)
    {
        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct, "fragment", "activate", "shell", "orders", release);
        Assert.True(exit == 0, error);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
    }

    private static async Task<string> ServedAsync(TestBrowser browser, GatewayHarness harness, string path)
    {
        BrowserResponse response = await browser.GetAsync(harness.App("shell").Url(path).ToString(), ct: Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {response}");
        return response.Body;
    }
}
