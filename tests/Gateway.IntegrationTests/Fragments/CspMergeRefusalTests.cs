using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Registry;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// N-36 (#1007, part of #961 -> "Composition"): a fragment's <c>csp</c> extends only <c>script-src</c>,
/// <c>style-src</c>, <c>img-src</c>, <c>font-src</c> and <c>connect-src</c>, and its <c>script-src</c>
/// carries no <c>'strict-dynamic'</c>, nonce or hash, because the one policy it joins is the whole host's.
/// A registry write that breaks either rule is refused by <c>spactl registry apply</c>, and the same
/// document written past spactl is rejected by the gateway's own read: in both cases the live document
/// stays. The register names <c>CspMergeTests.cs</c> for this row; it is in a file of its own because the
/// F-28 merge row is written beside it in that file by another change.
/// </summary>
public sealed class CspMergeRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
