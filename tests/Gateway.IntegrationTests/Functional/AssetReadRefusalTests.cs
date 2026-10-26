using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Platform.Testing;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-06's third clause, "a missing asset returns 404" (docs/plan/04-tests.md), through the composed
/// gateway with the cell's bucket answering as the deployed role makes S3 answer: the task role grants
/// <c>s3:GetObject</c> and no <c>s3:ListBucket</c>, so a missing key is a 403 and the store raises a
/// refusal (<see cref="MissingKeyAnswer.AmbiguousRefusal"/>; change request
/// contracts/changes/issue-381-object-read-refusal.md). On the deployed cell that refusal escaped as a 500
/// (#63, comment 6390244202); workstream 2's rule in <c>MapPlatformAssets</c> answers it as the 404
/// <c>not_found</c> the browser contract names, never stored, and leaves <c>read_refused</c> for
/// <c>UsePlatformTelemetry</c> to log (#429).
///
/// <para>The control is the same request against a bucket that confirms absence: the same 404 and the
/// same header, and a request line with no <c>read_refused</c>. That difference is the only thing on the
/// log stream that tells an operator a missing asset was a permission fault.</para>
/// </summary>
public sealed class AssetReadRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The path the deployed probe asked for (#63). It names a script no release holds.
    private const string MissingAsset = "/assets/missing.0badf00d.js";

    /// <summary>
    /// Asks a signed-in browser for the missing asset, asserts the answer is the browser contract's 404
    /// <c>not_found</c> with the asset row's <c>Cache-Control</c>, and returns the request's line. The
    /// answer is asserted first, so a gateway that answers anything else fails on what it answered.
    /// </summary>
    private static async Task<JsonElement> MissingAssetsLineAsync(GatewayHarness harness)
    {
        using TestBrowser browser = await harness.CreateSignedInBrowserAsync(Ct);
        int before = CapturedTelemetry.RequestLines(harness).Count;

        BrowserResponse response = await browser.NavigateAsync(harness.App("app1").Url(MissingAsset), ct: Ct);
        AssertTheContractsNotFound(response);

        // The telemetry stage writes the line after the rest of the pipeline returns, so it can land after
        // the response does. The asset's line is the only 404 on the asset route since the sign-in.
        static bool IsTheAssets(JsonElement line) =>
            line.GetProperty("route").GetString() == "asset" && line.GetProperty("status").GetInt32() == 404;
        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Skip(before).Any(IsTheAssets), "the missing asset's request line", Ct);
        return Assert.Single(CapturedTelemetry.RequestLines(harness).Skip(before), IsTheAssets);
    }

    private static void AssertTheContractsNotFound(BrowserResponse response)
    {
        Contract.ErrorRow notFound = Contract.Error("not_found");
        Assert.Equal((HttpStatusCode)notFound.Status, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(response.Body);
        Assert.Equal(notFound.Code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(Directives(Contract.AssetNotFoundCacheControl), Directives(response.Header("Cache-Control")));
    }

    /// <summary>
    /// The directives of a <c>Cache-Control</c> value, as a set. The browser reads headers through
    /// <c>HttpClient</c>, which parses the value into a <c>CacheControlHeaderValue</c> and writes its
    /// directives back in that type's own order ("no-store, private" for the wire's "private, no-store";
    /// https://learn.microsoft.com/dotnet/api/system.net.http.headers.cachecontrolheadervalue), and
    /// RFC 9111 section 5.2 gives the order no meaning.
    /// </summary>
    private static string[] Directives(string? value) =>
        [.. (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public async Task F06_in_refusal_mode_a_missing_asset_answers_404_not_found_never_stored_and_its_line_carries_read_refused_asset()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            new GatewayHarnessOptions { MissingKeys = MissingKeyAnswer.AmbiguousRefusal }, Ct);

        // Red before workstream 2's rule, green after it. Mutation that turns it red: Assets.cs as it was
        // before task issue-428 (git show 9ea2f17^:src/Platform.Hosting/Assets.cs), which lets the refusal
        // escape, answers 500 here, as the deployed cell did.
        JsonElement line = await MissingAssetsLineAsync(harness);

        // The line names the class of key the store refused, beside the feature's other two fields, and
        // otherwise carries the telemetry contract's fields in its order. Mutation: dropping read_refused
        // from UsePlatformTelemetry's line fails the first assertion with "The given key was not present".
        Assert.Equal("asset", line.GetProperty("read_refused").GetString());
        string[] names = [.. line.EnumerateObject().Select(field => field.Name).Where(name => name != "category")];
        Assert.Equal(Array.IndexOf(names, "session_ref") + 1, Array.IndexOf(names, "read_refused"));
        Assert.Equal(Contract.LogFields.Where(name => name != "read_refused"), names.Where(name => name != "read_refused"));
        Assert.Matches("^[0-9a-f]{12}$", line.GetProperty("session_ref").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rule").ValueKind);

        // N-16 over the refused read: the key holds the client's path, and no line of the gateway's
        // carries it, the request line included. Mutation that turns this search red: the telemetry stage
        // logging the request path on a line of its own for a refused read, read_refused still "asset".
        // Logging the path as read_refused fails the value assertion above first.
        Assert.DoesNotContain(harness.Log.Rendered, rendered => rendered.Contains("missing.0badf00d", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F06_control_a_bucket_that_confirms_absence_answers_the_same_404_and_its_line_carries_no_read_refused()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);

        // Mutation that turns it red: the asset route writing read_refused for a null as well as for a
        // refusal puts the field on this line; before task issue-428 the 404 carried no Cache-Control.
        JsonElement line = await MissingAssetsLineAsync(harness);
        Assert.False(line.TryGetProperty("read_refused", out _), "a confirmed-missing asset's line carries read_refused.");
    }
}
