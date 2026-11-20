using System.Net;
using System.Text.Json;
using Platform.Abstractions;

namespace Platform.Security.Tests;

/// <summary>
/// <c>UsePlatformRequestGate</c>'s rows for the fragments spec (#996, part of #961):
/// <c>/fragments/{seg}/api/**</c> gated exactly as <c>/api/**</c>, <c>GET /bff/fragments</c> as
/// <c>/bff/session</c>, <c>/signin-oidc/{fragmentId}</c> as <c>/signin-oidc</c>, and <c>/bff/chain</c>,
/// a navigation from any site (ADR-0040). contracts/request-gate.md -> Rules by path is the contract.
/// </summary>
public sealed class FragmentGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string, string?) FetchSite(string? value) => ("Sec-Fetch-Site", value);

    private static (string, string?) Origin(string? value) => ("Origin", value);

    private static readonly (string, string?) Navigate = ("Sec-Fetch-Mode", "navigate");

    private static readonly (string, string?) Document = ("Sec-Fetch-Dest", "document");

    private static async Task AssertRefusedAsync(
        GateHost host, HttpResponseMessage response, HttpStatusCode status, string code, string rule)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, host.Reached);
        Assert.Single(host.Log.LinesFor(rule));
    }

    private static async Task AssertPassedAsync(GateHost host, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GateHost.Passed, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, host.Reached);
    }

    // ---- /fragments/{seg}/api/**: identical to /api/** ----

    [Theory]
    [InlineData("GET", "/fragments/frag1/api/orders/items", "cross-site")]
    [InlineData("GET", "/fragments/frag1/api/orders/items", "same-site")]
    [InlineData("GET", "/fragments/frag1/api/orders/items", "none")]
    [InlineData("GET", "/fragments/frag1/api/orders/items", null)]
    [InlineData("POST", "/fragments/frag1/api/orders/items", "same-site")]
    [InlineData("OPTIONS", "/fragments/frag1/api", "cross-site")]
    [InlineData("GET", "/fragments/not-a-fragment/api/orders", "cross-site")]
    [InlineData("GET", "/FRAGMENTS/frag1/API/orders/items", "cross-site")]
    [InlineData("GET", "/Fragments/frag1/Api", null)]
    public async Task A_fragment_api_request_not_from_the_app_itself_gets_403_under_gate_fetch_site(
        string method, string path, string? fetchSite)
    {
        // Classified by path shape: the segment need not name a fragment, and the letter case is
        // routing's, so no variant reaches the proxy as an asset request.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite(fetchSite), Origin(GateHost.AppOrigin));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", RuleIds.GateFetchSite);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task A_fragment_api_request_from_a_foreign_origin_gets_403_under_gate_origin(string method)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, "/fragments/frag1/api/orders/items", Ct,
            FetchSite("same-origin"), Origin(GateHost.SiblingOrigin));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", RuleIds.GateOrigin);
    }

    [Theory]
    [InlineData("GET", "https://app1.spa-poc.test")]
    [InlineData("POST", "https://app1.spa-poc.test")]
    [InlineData("PUT", "https://app1.spa-poc.test")]
    [InlineData("PATCH", "https://app1.spa-poc.test")]
    [InlineData("DELETE", "https://app1.spa-poc.test")]
    [InlineData("GET", null)]
    public async Task A_same_origin_fragment_api_request_passes_with_any_method(string method, string? origin)
    {
        // Any method: an asset row would refuse POST with 405, so a misclassified path fails here.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, "/fragments/frag1/api/orders/items", Ct,
            FetchSite("same-origin"), Origin(origin));

        await AssertPassedAsync(host, response);
    }

    [Theory]
    [InlineData("/fragments/frag1/assets/a.js")]
    [InlineData("/fragments/frag1/entry.js")]
    [InlineData("/fragments/frag1/apix/orders")]
    public async Task A_fragment_path_outside_its_api_is_an_asset_and_passes_from_any_site(string path)
    {
        // The control for the rows above: only the api segment makes a fragment path an API path.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", path, Ct, FetchSite("cross-site"));

        await AssertPassedAsync(host, response);
    }

    // ---- GET /bff/fragments, as /bff/session ----

    [Fact]
    public async Task A_same_origin_get_on_bff_fragments_passes()
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/fragments", Ct, FetchSite("same-origin"));

        await AssertPassedAsync(host, response);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData(null)]
    public async Task Bff_fragments_not_from_the_app_itself_gets_gate_fetch_site(string? fetchSite)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/fragments", Ct, FetchSite(fetchSite));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", RuleIds.GateFetchSite);
    }

    // ---- gate.method on the two new GET-only rows ----

    [Theory]
    [InlineData("POST", "/bff/fragments")]
    [InlineData("DELETE", "/bff/fragments")]
    [InlineData("PUT", "/BFF/Fragments")]
    [InlineData("POST", "/signin-oidc/frag1")]
    [InlineData("PUT", "/signin-oidc/frag1")]
    [InlineData("DELETE", "/signin-oidc/frag1")]
    public async Task Bff_fragments_and_a_fragment_callback_refuse_every_other_method_under_gate_method(
        string method, string path)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin));

        await AssertRefusedAsync(host, response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", RuleIds.GateMethod);
        Assert.Equal("GET", string.Join(", ", response.Content.Headers.Allow));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData(null)]
    public async Task A_fragment_callback_get_passes_from_any_site(string? fetchSite)
    {
        // The framework checks state, as for /signin-oidc; the provider's redirect is cross-site.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/signin-oidc/frag1?code=c&state=s", Ct,
            FetchSite(fetchSite));

        await AssertPassedAsync(host, response);
    }

    // ---- /bff/chain: a navigation from any site (ADR-0040) ----

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task A_chain_step_navigation_passes_whatever_its_sec_fetch_site(string? fetchSite)
    {
        // A step reached through the provider's redirect is cross-site at every later hop.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/chain?c=continuation", Ct,
            FetchSite(fetchSite), Navigate, Document);

        await AssertPassedAsync(host, response);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("navigate", null)]
    [InlineData(null, "document")]
    [InlineData("cors", "empty")]
    [InlineData("navigate", "iframe")]
    [InlineData("no-cors", "document")]
    public async Task A_chain_step_that_is_not_a_document_navigation_gets_gate_navigation(string? mode, string? dest)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/chain?c=continuation", Ct,
            FetchSite("cross-site"), ("Sec-Fetch-Mode", mode), ("Sec-Fetch-Dest", dest));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", RuleIds.GateNavigation);
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("POST", false)]
    [InlineData("DELETE", true)]
    public async Task A_chain_step_by_any_method_but_get_gets_gate_method(string method, bool navigation)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = navigation
            ? await host.SendAsync(method, "/bff/chain?c=continuation", Ct, FetchSite("cross-site"), Navigate, Document)
            : await host.SendAsync(method, "/bff/chain?c=continuation", Ct, FetchSite("cross-site"));

        await AssertRefusedAsync(host, response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", RuleIds.GateMethod);
        Assert.Equal("GET", string.Join(", ", response.Content.Headers.Allow));
    }
}
