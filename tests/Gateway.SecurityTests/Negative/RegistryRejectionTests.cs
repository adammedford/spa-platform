using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;
using Platform.Testing;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-14, whole: "Publish a registry with a duplicate host, a disallowed upstream, or a forbidden CSP
/// source -> Rejected whole. The live snapshot is unchanged." (docs/plan/04-tests.md.)
///
/// <para><b>"Whole" is the part a status code cannot show</b>, so every row here publishes a
/// document that carries <i>two</i> changes: the one the row names, and a perfectly legitimate one
/// beside it. A validator that rejected the bad entry and applied the good one would pass a test
/// that only checked the bad entry had not taken effect. What is asserted instead is that the good
/// change did not land either — the live snapshot is the one that was serving before, entry for
/// entry.</para>
///
/// <para><b>Read from outside.</b> The revision comes from <c>/readyz</c>, which
/// <c>contracts/pipeline.md</c> defines as "200 with <c>{"revision": N}</c> once a snapshot is
/// live" and which <c>spactl wait</c> reads; the policies come from each app's shell as a browser
/// signed in to both apps receives it, since a shell is served only in a session (ADR-0008). The
/// poll's own outcome is asserted as well, because a document that was never read would leave every
/// other assertion true.</para>
///
/// <para><b>Its own harness per row.</b> Each row publishes a registry, so none of them can share
/// one: a rejected document still advances the ETag the source will not read again.</para>
/// </summary>
public sealed class RegistryRejectionTests
{
    /// <summary>A legitimate change, published in the same document as each bad one.</summary>
    private const string BenignAddition = "https://benign.example";

    [Fact]
    public async Task A_duplicate_host_rejects_the_document_whole()
    {
        await AssertRejectedWholeAsync((document, harness) =>
            // Two entries claiming one authority. Host resolution finds an app by exact authority, so a
            // document like this has no answer to "which app is app1's host?" and the only safe
            // reading is to take none of it.
            Entry(document, "app2")["host"] = harness.App("app1").Host);
    }

    [Fact]
    public async Task An_upstream_outside_the_platforms_allowed_list_rejects_the_document_whole()
    {
        await AssertRejectedWholeAsync((document, _) =>
            // An origin Policy:AllowedUpstreams does not name. The bound is platform configuration's,
            // not the publisher's, which is what stops an app pointing /api at anything it likes.
            Entry(document, "app1")["apis"]!.AsArray()[0]!["upstream"] = "http://127.0.0.1:1");
    }

    [Fact]
    public async Task A_forbidden_content_security_policy_source_rejects_the_document_whole()
    {
        await AssertRejectedWholeAsync((document, _) =>
            // contracts/http-contract.md: "`*` and `'unsafe-eval'` are rejected in every directive".
            // A policy source is the one registry value that reaches the browser verbatim.
            //
            // Added beside the benign source rather than replacing the directive. Assigning `csp`
            // here would overwrite what Benign just wrote, leaving this the one row in the class
            // that published a single change — and its "the benign change is absent too"
            // assertion vacuous, because the value would never have been in the document.
            Entry(document, "app1")["csp"]!["img-src"]!.AsArray().Add("*"));
    }

    [Fact]
    public async Task A_document_that_is_not_json_at_all_rejects_and_leaves_the_snapshot_serving()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Suite.Ct);
        Before before = await ReadAsync(harness, browser);

        RefreshOutcome outcome = await harness.PublishRegistryTextAsync("{ this is not a registry", Suite.Ct);

        Assert.Equal(RefreshOutcome.Rejected, outcome);
        await AssertUnchangedAsync(harness, browser, before);
    }

    [Fact]
    public async Task A_sound_document_does_swap_and_does_change_what_is_served()
    {
        // The control. Every row above asserts that nothing changed, which a gateway that could
        // never swap a registry would also satisfy, and so would a test whose edits never reached
        // the bucket (CODING_STANDARDS.md -> "An assertion must be able to fail"). This is the same
        // benign change those documents carried, published on its own.
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Suite.Ct);
        Before before = await ReadAsync(harness, browser);

        RefreshOutcome outcome = await harness.UpdateRegistryAsync(document => Benign(document), Suite.Ct);

        Assert.Equal(RefreshOutcome.Swapped, outcome);
        Assert.Equal(before.Revision + 1, await RevisionAsync(harness));
        Assert.Contains(BenignAddition, await PolicyAsync(harness, browser, "app1"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Publishes one document carrying <paramref name="spoil"/> and the benign change beside it, and
    /// asserts that the poll rejected it and that neither change reached what is being served.
    /// </summary>
    private static async Task AssertRejectedWholeAsync(Action<JsonObject, GatewayHarness> spoil)
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Suite.Ct);
        Before before = await ReadAsync(harness, browser);

        RefreshOutcome outcome = await harness.UpdateRegistryAsync(
            document =>
            {
                Benign(document);
                spoil(document, harness);
            },
            Suite.Ct);

        Assert.Equal(RefreshOutcome.Rejected, outcome);
        await AssertUnchangedAsync(harness, browser, before);
    }

    /// <summary>
    /// A change any validator would accept, in the same document as the bad one: a source added to a
    /// directive an entry may extend. Its absence afterwards is what makes "rejected whole" readable.
    /// </summary>
    private static void Benign(JsonObject document) =>
        Entry(document, "app1")["csp"] = new JsonObject { ["img-src"] = new JsonArray(BenignAddition) };

    private static JsonObject Entry(JsonObject document, string appId) =>
        document["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == appId)!.AsObject();

    private static async Task<Before> ReadAsync(GatewayHarness harness, TestBrowser browser) => new(
        await RevisionAsync(harness),
        await PolicyAsync(harness, browser, "app1"),
        await PolicyAsync(harness, browser, "app2"));

    private static async Task AssertUnchangedAsync(GatewayHarness harness, TestBrowser browser, Before before)
    {
        // The live revision, as spactl wait reads it.
        Assert.Equal(before.Revision, await RevisionAsync(harness));

        // And both apps still serving, with the policies they had: the benign change is absent, so
        // the document was taken whole or not at all.
        string app1 = await PolicyAsync(harness, browser, "app1");
        Assert.Equal(before.App1Policy, app1);
        Assert.Equal(before.App2Policy, await PolicyAsync(harness, browser, "app2"));
        Assert.DoesNotContain(BenignAddition, app1, StringComparison.Ordinal);
    }

    /// <summary>The live revision, read from <c>/readyz</c> rather than from the registry object.</summary>
    private static async Task<long> RevisionAsync(GatewayHarness harness)
    {
        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/readyz", harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(200, response.StatusCode);
        return JsonNode.Parse(response.BodyText)!["revision"]!.GetValue<long>();
    }

    /// <summary>
    /// The policy one app is serving, which is the entry as the gateway has it: read off the app's
    /// shell, requested with the cookies <paramref name="browser"/> holds for that app.
    /// </summary>
    private static async Task<string> PolicyAsync(GatewayHarness harness, TestBrowser browser, string appId)
    {
        HarnessApp app = harness.App(appId);
        RawResponse response = await RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/", app.Host, [("Cookie", SignedIn.CookieHeader(browser, app))], Suite.Ct);

        Assert.Equal(200, response.StatusCode);
        return Assert.Single(response.Values("Content-Security-Policy"));
    }

    /// <summary>What was live before a publish.</summary>
    private sealed record Before(long Revision, string App1Policy, string App2Policy);
}
