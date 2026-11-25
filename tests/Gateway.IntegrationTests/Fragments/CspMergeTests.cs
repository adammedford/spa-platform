using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// The merged content security policy on the composed gateway (#1007, part of #961;
/// contracts/http-contract.md -> "The baseline content security policy"): one policy for the host,
/// the baseline, then the shell's additions, then each fragment's in registry order.
/// </summary>
public sealed class CspMergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// F-28: every response on the shell's host, shell and fragment paths alike, carries one
    /// <c>Content-Security-Policy</c>: per directive the baseline, the shell's additions, then each
    /// fragment's in registry order, a source two of them add appearing once.
    /// </summary>
    [Fact]
    public async Task G9_every_response_of_a_shell_host_carries_one_merged_policy_in_registry_order()
    {
        // Mutation that turns it red: merging the fragments in ordinal fragmentId order rather than
        // registry order is the same here, so the registry order below puts reports first; merging by
        // set union without order, or appending a source twice, fails the img-src list; a fragment path
        // that carries the shell's policy alone fails its connect-src.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        await Fragments.LoginAsync(browser, shell, "/", Ct);

        // The shell's own additions first, with no fragment adding any: what the merge starts from.
        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(next =>
            Entry(next)["csp"]!["img-src"] = new JsonArray("https://a.example"), Ct));
        Dictionary<string, string[]> before = Policy(Assert.Single(await PoliciesAsync(browser, shell)));
        Assert.Equal("https://a.example", before["img-src"][^1]);

        // Reports listed before orders, each adding sources, one of them a source the shell and orders
        // both add.
        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(next =>
        {
            JsonArray fragments = Entry(next)["fragments"]!.AsArray();
            JsonNode orders = fragments[0]!;
            fragments.RemoveAt(0);
            fragments.Add(orders);
            fragments[0]!["csp"] = new JsonObject { ["img-src"] = new JsonArray("https://c.example", "https://b.example") };
            fragments[1]!["csp"] = new JsonObject
            {
                ["img-src"] = new JsonArray("https://b.example", "https://a.example", "https://d.example"),
                ["connect-src"] = new JsonArray("https://api.example"),
            };
        }, Ct));

        IReadOnlyList<string> policies = await PoliciesAsync(browser, shell);
        Dictionary<string, string[]> merged = Policy(Assert.Single(policies));

        Assert.Equal([.. before["img-src"], "https://c.example", "https://b.example", "https://d.example"], merged["img-src"]);
        Assert.Equal([.. before["connect-src"], "https://api.example"], merged["connect-src"]);
        foreach ((string directive, string[] sources) in before.Where(d => d.Key is not "img-src" and not "connect-src"))
        {
            Assert.Equal(sources, merged[directive]);
        }

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), merged.Keys.Order(StringComparer.Ordinal));
    }

    private static JsonObject Entry(JsonObject document) =>
        document["apps"]!.AsArray().OfType<JsonObject>().Single(entry => entry["appId"]?.GetValue<string>() == "shell");

    /// <summary>
    /// The distinct policies the host answered across its kinds of response: the shell's document and
    /// fallback, a fragment's entry and chunk, the BFF's JSON, an API call, a fragment API call and an
    /// unknown fragment's 404. Each response must carry exactly one header.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PoliciesAsync(TestBrowser browser, HarnessApp shell)
    {
        string[] paths =
        [
            "/", "/orders/42", "/fragments/orders/entry.js", "/fragments/reports/assets/panel.d2ff9911.js",
            "/bff/session", "/bff/fragments", "/api/echo/items/1", "/fragments/orders/api/echo/items/2", "/fragments/nope/a.js",
        ];
        HashSet<string> policies = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            BrowserResponse response = await browser.GetAsync(shell.Url(path).ToString(), ct: Ct);
            Assert.True(
                response.Headers.TryGetValue("Content-Security-Policy", out System.Collections.Immutable.ImmutableArray<string> values) && values.Length == 1,
                $"{path} answered {response} with {(values.IsDefault ? 0 : values.Length)} policies.");
            policies.Add(values[0]);
        }

        return [.. policies];
    }

    private static Dictionary<string, string[]> Policy(string header) =>
        header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.Ordinal);

    /// <summary>
    /// N-36 (#1007, part of #961 -> "Composition"): a fragment's <c>csp</c> extends only <c>script-src</c>,
    /// <c>style-src</c>, <c>img-src</c>, <c>font-src</c> and <c>connect-src</c>, and its <c>script-src</c>
    /// carries no <c>'strict-dynamic'</c>, nonce or hash, because the one policy it joins is the whole host's.
    /// A registry write that breaks either rule is refused by <c>spactl registry apply</c>, and the same
    /// document written past spactl is rejected by the gateway's own read: in both cases the live document
    /// stays.
    /// </summary>

    public static TheoryData<string, string> Widenings => new()
    {
        { "frame-ancestors", "'self'" },
        { "default-src", "'self'" },
        { "base-uri", "'self'" },
        { "form-action", "'self'" },
        { "object-src", "'self'" },
        { "script-src", "'strict-dynamic'" },
        { "script-src", "'nonce-c3BhLXBsYXRmb3Jt'" },
        { "script-src", "'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU='" },
    };

    [Theory]
    [MemberData(nameof(Widenings))]
    public async Task G9_a_fragment_csp_outside_the_five_directives_or_with_strict_dynamic_a_nonce_or_a_hash_is_refused(string directive, string source)
    {
        // Mutation that turns it red: a fragment csp rule that checks only the directive names (not
        // script-src's sources) lets the last three rows through; one that checks nothing lets every row
        // through, and the live revision moves.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        JsonObject good = harness.RegistryDocument;
        long live = harness.Registry.Current.Revision;
        string? before = await SpactlRuns.TextAsync(harness, GatewayHarness.RegistryKey, Ct);

        JsonObject widened = (JsonObject)good.DeepClone();
        widened["revision"] = live + 1;
        widened["apps"]![0]!["fragments"]![0]!["csp"] = new JsonObject { [directive] = new JsonArray(source) };

        (int exit, _, string error) = await SpactlRuns.RunAsync(harness, Ct, "registry", "apply", SpactlRuns.Document(widened));
        Assert.Equal(1, exit);
        Assert.Contains("/apps/0/fragments/0/csp", error, StringComparison.Ordinal);
        Assert.Equal(before, await SpactlRuns.TextAsync(harness, GatewayHarness.RegistryKey, Ct));

        // Past spactl, as an operator with bucket access could write it: the gateway's validator rejects it.
        Assert.Equal(RefreshOutcome.Rejected, await harness.PublishRegistryAsync(widened, Ct));
        Assert.Equal(live, harness.Registry.Current.Revision);
        Assert.Empty(harness.Registry.Current.ByAppId["shell"].Fragments.Single().Csp);

        // The control: the same fragment adding a source to connect-src is accepted, and the host's one
        // policy carries it.
        JsonObject allowed = (JsonObject)good.DeepClone();
        allowed["revision"] = live + 2;
        allowed["apps"]![0]!["fragments"]![0]!["csp"] = new JsonObject { ["connect-src"] = new JsonArray("https://orders.spa-poc.test") };
        (int ok, _, string okError) = await SpactlRuns.RunAsync(harness, Ct, "registry", "apply", SpactlRuns.Document(allowed));
        Assert.True(ok == 0, okError);
        Assert.Equal(RefreshOutcome.Swapped, await harness.ReloadRegistryDocumentAsync(Ct));
        Assert.Equal(live + 2, harness.Registry.Current.Revision);
    }
}
