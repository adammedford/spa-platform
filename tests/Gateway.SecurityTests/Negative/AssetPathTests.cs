using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-13, whole: "Asset paths with dot segments, encoded slashes, backslashes, doubled slashes and
/// null bytes -> 400 or 404. No object outside the app's release prefix is read."
/// (docs/plan/04-tests.md.)
///
/// <para><b>Two claims, and the second is the one that needs machinery.</b> The status code is read
/// off the response. "No object outside the release prefix is read" is a claim about a read that did
/// not happen, so this class watches the bucket: an <see cref="ObjectStoreRecorder"/> wraps the
/// store the harness registered and writes down every key the gateway asked for. The sharper form
/// the ticket asks for — refused <b>before</b> any read is attempted — is then readable as "this
/// request added no key at all", which is stronger than "added no key outside the prefix" and is
/// what <c>contracts/s3-layout.md</c> means by building a key "never from an unchecked path
/// segment".</para>
///
/// <para><b>Why the raw socket.</b> Every shape in the row is one <see cref="Uri"/> normalises away:
/// a client library would send <c>/secret</c> where the row says <c>/../secret</c>. Two stages judge
/// these paths and both read the undecoded target — the request gate at stage 6 reads
/// <c>RawTarget</c> "before any decoding", and the asset endpoint judges it again itself, "because
/// the key is its own and N-13 is about what it reads, whichever stage refused". Only a socket can
/// put the row's bytes on the wire.</para>
///
/// <para><b>Which stage answers is not asserted, and that is deliberate.</b> In the composed gateway
/// stage 6 refuses first, so the refusals below carry <see cref="RuleIds.PathInvalid"/> from there;
/// the endpoint's own copy of the check is what makes the claim hold if the order ever changed. Both
/// raise the same rule id, which is exactly why <c>RuleIds</c> exists (ADR-0015).</para>
/// </summary>
public sealed class AssetPathTests : IAsyncLifetime
{
    private readonly ObjectStoreRecorder recorder = new();
    private GatewayHarness harness = null!;

    /// <summary>
    /// One harness per <b>test</b> here, not per class, which is the one place in this suite that
    /// differs. Not a class fixture, because the recorder and the harness have to be built together
    /// and a fixture would have to hand both back; <see cref="IAsyncLifetime"/> on the class is
    /// re-run for every test, so each gets a fresh pair — which also keeps the keys recorded for one
    /// row out of another's, and is why "this request read nothing" can be read off a count.
    /// </summary>
    public async ValueTask InitializeAsync() =>
        harness = await Suite.StartAsync(recorder.Options(), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await harness.DisposeAsync();

    /// <summary>
    /// The shapes the row names, each sent exactly as written: plain and encoded dot segments, a dot
    /// segment with the <c>;</c> parameter some servers strip before resolving it, encoded slashes
    /// and backslashes, a literal backslash, and a doubled slash. The targets aim at a sibling app's
    /// release, which is the object a traversal would be after.
    /// </summary>
    private static readonly string[] TraversalTargets =
        [
            "/../secret",
            "/..%2fsecret",
            "/%2e%2e/secret",
            "/.;/secret",
            "/..;/secret",
            "/assets/../../../app2/releases/x/index.html",
            "/assets%2f..%2findex.html",
            "/assets%5c..%5cindex.html",
            "/assets\\..\\index.html",
            "/assets//app.js",
            "/./index.html",
        ];

    public static TheoryData<string> Traversals => new(TraversalTargets);

    [Theory]
    [MemberData(nameof(Traversals))]
    public async Task A_traversal_shape_is_refused_and_reads_nothing_at_all(string target)
    {
        int readsBefore = recorder.Reads.Count;
        int lines = await Observed.SettledCountAsync(harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", target, harness.App("app1").Host, ct: Suite.Ct);

        // The row allows 400 or 404; the gate's normalisation rule gives the 400 and the contract's
        // code for it, both read from the Errors table rather than written down.
        Contract.ErrorRow expected = Contract.Error("The path failed normalisation");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);
        Assert.Equal(RuleIds.PathInvalid, await Observed.RuleOfOneRequestAsync(harness, lines, Suite.Ct));

        // The claim this class exists for: not one key was asked for, inside the prefix or out.
        Assert.Empty(recorder.Since(readsBefore));
    }

    [Fact]
    public async Task A_null_byte_in_an_asset_path_never_reaches_the_pipeline_or_the_bucket()
    {
        // Kestrel refuses an escaped NUL itself, before any middleware runs
        // (src/Platform.Hosting/AssetPath.cs records the same), so this one is answered by the
        // server: the row's "400 or 404" is met, there is no rule id to read because no stage ran,
        // and the bucket is untouched. Asserted apart from the rows above for that reason — a test
        // that demanded a rule id here would be holding the wrong component to the row.
        int readsBefore = recorder.Reads.Count;
        int lines = await Observed.SettledCountAsync(harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/index%00.html", harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(recorder.Since(readsBefore));
        Assert.Equal(lines, await Observed.SettledCountAsync(harness, Suite.Ct));
    }

    [Fact]
    public async Task A_legitimate_asset_path_does_read_one_key_inside_the_release_prefix()
    {
        // The control, and without it every row above is vacuous: a gateway that read nothing ever,
        // or a recorder that recorded nothing, would satisfy all of them
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        int readsBefore = recorder.Reads.Count;

        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/index.html", harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(200, response.StatusCode);
        string key = Assert.Single(recorder.Since(readsBefore));
        Assert.Equal(ReleasePrefix("app1") + "index.html", key);
    }

    [Fact]
    public async Task A_missing_asset_reads_inside_the_prefix_and_answers_404()
    {
        // The other half of the row's "400 or 404": a path that is well formed but names nothing.
        // It does reach the bucket, once, under this app's own prefix — which is what distinguishes
        // "refused" from "looked for and not found".
        int readsBefore = recorder.Reads.Count;

        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/assets/not-shipped.js", harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(404, response.StatusCode);
        string key = Assert.Single(recorder.Since(readsBefore));
        Assert.StartsWith(ReleasePrefix("app1"), key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Across_everything_this_harness_served_no_key_outside_an_apps_release_was_read()
    {
        // The row's own words, over a whole cell's traffic rather than one request: every traversal
        // shape, a legitimate asset, a missing one, and a sibling app's shell.
        foreach (string target in TraversalTargets)
        {
            await RawHttp.SendAsync(harness.GatewayAddress, "GET", target, harness.App("app1").Host, ct: Suite.Ct);
        }

        await RawHttp.SendAsync(harness.GatewayAddress, "GET", "/index.html", harness.App("app1").Host, ct: Suite.Ct);
        await RawHttp.SendAsync(harness.GatewayAddress, "GET", "/index.html", harness.App("app2").Host, ct: Suite.Ct);
        await RawHttp.SendAsync(harness.GatewayAddress, "GET", "/missing.js", harness.App("app1").Host, ct: Suite.Ct);

        string[] allowed = [ReleasePrefix("app1"), ReleasePrefix("app2"), GatewayHarness.RegistryKey];

        Assert.NotEmpty(recorder.Reads);
        foreach (string key in recorder.Reads)
        {
            Assert.Contains(allowed, prefix => key.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Where this app's release lives, built the way <c>contracts/s3-layout.md</c> lays the bucket
    /// out, from the app id and release the harness published rather than from a literal.
    /// </summary>
    private string ReleasePrefix(string appId) =>
        $"apps/{harness.App(appId).AppId}/releases/{harness.Options.Release}/";
}
