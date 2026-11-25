using System.Net;
using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Fragments;

/// <summary><c>GET /bff/fragments</c> on the composed gateway (#1007, part of #961; contracts/http-contract.md).</summary>
public sealed class FragmentsEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// F-27: with a session, <c>{fragments:[{fragmentId, entryUrl}]}</c> in registry order, each
    /// <c>entryUrl</c> <c>/fragments/{fragmentId}/{entry}</c> naming no release, with
    /// <c>Cache-Control: no-store</c>; without one, 401 <c>unauthenticated</c>. GET only: a POST is
    /// refused before the endpoint is reached.
    /// </summary>
    [Fact]
    public async Task G9_bff_fragments_lists_each_fragment_s_id_and_entry_url_to_a_session_only_with_no_store()
    {
        // Mutation that turns it red: an entryUrl built from the release path (or the default entry for
        // every fragment) fails the reports item, whose entry is main.js; dropping the no-store header
        // fails the Cache-Control check.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        HarnessApp shell = harness.App("shell");

        using TestBrowser anonymous = harness.CreateBrowser();
        await anonymous.NavigateAsync(shell.Url("/bff/login"), Steps.Manual, Ct);
        BrowserResponse refused = await anonymous.GetAsync(shell.Url("/bff/fragments").ToString(), ct: Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("unauthenticated", TwoHostShell.Code(refused));

        using TestBrowser browser = harness.CreateBrowser();
        await Fragments.LoginAsync(browser, shell, "/", Ct);
        BrowserResponse listed = await browser.GetAsync(shell.Url("/bff/fragments").ToString(), ct: Ct);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains("no-store", listed.Header("Cache-Control") ?? "", StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(listed.Body);
        (string, string)[] items = [.. body.RootElement.GetProperty("fragments").EnumerateArray()
            .Select(item => (item.GetProperty("fragmentId").GetString()!, item.GetProperty("entryUrl").GetString()!))];
        Assert.Equal([("orders", "/fragments/orders/entry.js"), ("reports", "/fragments/reports/main.js")], items);
        Assert.All(items, item => Assert.DoesNotContain(harness.Options.Release, item.Item2, StringComparison.Ordinal));

        // The listed entry resolves to the live release's bytes.
        BrowserResponse entry = await browser.GetAsync(shell.Url("/fragments/reports/main.js").ToString(), ct: Ct);
        Assert.Equal(FixtureFiles.Fragment("shell", "reports")["main.js"], entry.Bytes);

        // GET only. Mutation: mapping the endpoint for any method answers this POST with the list.
        BrowserResponse posted = await browser.FetchAsync(HttpMethod.Post, shell.Url("/bff/fragments").ToString(), new StringContent(""), ct: Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
        JsonElement line = harness.Log.Json.Last(j => j.GetProperty("msg").GetString() == "request");
        Assert.Equal("gate.method", line.GetProperty("rule").GetString());
    }
}
