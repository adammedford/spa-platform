using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-07, <b>partial</b>: "Call <c>/api</c> and <c>/bff</c> with <c>Sec-Fetch-Site</c> of
/// <c>cross-site</c>, <c>same-site</c> or absent, or a foreign <c>Origin</c> -> 403
/// <c>origin_rejected</c>. No <c>Access-Control-Allow-*</c> header on any response, including
/// preflight." (docs/plan/04-tests.md.)
///
/// <para>These requests prove the stage-6 gate refusals on API and BFF paths, before
/// authentication (contracts/pipeline.md). They do not establish an authenticated session or
/// exercise a successful BFF endpoint; those require separate N-07 acceptance evidence.</para>
///
/// <para><b>The two refusals are told apart.</b> Both answer 403 <c>origin_rejected</c>, because the
/// <c>code</c> "says little on purpose"; the rule id is what distinguishes them
/// (<see cref="RuleIds.GateFetchSite"/> from <see cref="RuleIds.GateOrigin"/>), and the contract
/// says tests assert on both. A test that read only the status could not tell a gate that lost its
/// <c>Origin</c> check from one that kept it.</para>
///
/// <para>These requests isolate the HTTP contract named above. Session establishment and
/// preservation require separate acceptance evidence; a successful response here proves only the
/// asserted routing, policy or telemetry behaviour.</para>
/// </summary>
public sealed class ApiOriginTests(ApiOriginTests.Cell cell) : IClassFixture<ApiOriginTests.Cell>
{
    /// <summary>One harness for this class: every row sends the same registry a different header set.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>An API path the app does list, so a refusal here is the gate's and not the proxy's.</summary>
    private const string ApiPath = "/api/echo/thing";

    /// <summary>The <c>/bff</c> path whose row needs the same <c>same-origin</c> as <c>/api</c>.</summary>
    private const string BffPath = "/bff/session";

    /// <summary>
    /// The values <c>Sec-Fetch-Site</c> may not carry on these paths: the two a sibling or a
    /// stranger produces, and absent, which <c>contracts/request-gate.md</c> says "is a rejection,
    /// not a pass". Null is the absent case: the test browser removes the header for a null value.
    /// </summary>
    public static TheoryData<string, string?> PathsAndRefusedSites =>
        new()
        {
            { ApiPath, FetchMetadata.CrossSite },
            { ApiPath, FetchMetadata.SameSite },
            { ApiPath, null },
            { BffPath, FetchMetadata.CrossSite },
            { BffPath, FetchMetadata.SameSite },
            { BffPath, null },
        };

    [Theory]
    [MemberData(nameof(PathsAndRefusedSites))]
    public async Task A_refused_fetch_site_is_403_origin_rejected_under_gate_fetch_site(string path, string? site)
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.GetAsync(
            path, RequestOptions.With(("Sec-Fetch-Site", site)), Suite.Ct);

        await AssertRefusedAsync(response, before, RuleIds.GateFetchSite);
    }

    [Fact]
    public async Task A_fetch_from_a_page_on_another_site_is_refused_with_the_header_a_browser_computes()
    {
        // Not an overridden header: the test browser computes Sec-Fetch-Site from where the request
        // came from, as the Fetch Metadata specification does, so this is the request a real page on
        // another site would send. The identity provider's loopback origin is a different site from
        // the app's name, which is what makes it cross-site.
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(new Uri(Harness.Idp.BaseUrl), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.FetchAsync(
            HttpMethod.Get, Harness.App("app1").Url(ApiPath).ToString(), ct: Suite.Ct);

        Assert.Equal(FetchMetadata.CrossSite, response.Sent("Sec-Fetch-Site"));
        await AssertRefusedAsync(response, before, RuleIds.GateFetchSite);
    }

    [Fact]
    public async Task A_fetch_from_a_sibling_spas_page_is_refused_although_it_is_same_site()
    {
        // ADR-0007's reason for judging Sec-Fetch-Site at all: app1 and app2 share the registrable
        // domain, so a page on one is same-site with the other and SameSite cookies would travel.
        // The header a real sibling page produces is asserted, not supplied.
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app2").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.FetchAsync(
            HttpMethod.Get, Harness.App("app1").Url(ApiPath).ToString(), ct: Suite.Ct);

        Assert.Equal(FetchMetadata.SameSite, response.Sent("Sec-Fetch-Site"));
        await AssertRefusedAsync(response, before, RuleIds.GateFetchSite);
    }

    [Fact]
    public async Task A_foreign_origin_on_an_otherwise_same_origin_fetch_is_403_under_gate_origin()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        // Sec-Fetch-Site is left as the browser computes it (same-origin), so the only thing wrong
        // with this request is the Origin. That is what separates gate.origin from gate.fetch-site.
        BrowserResponse response = await browser.GetAsync(
            ApiPath, RequestOptions.With(("Origin", "https://evil.test")), Suite.Ct);

        Assert.Equal(FetchMetadata.SameOrigin, response.Sent("Sec-Fetch-Site"));
        await AssertRefusedAsync(response, before, RuleIds.GateOrigin);
    }

    [Fact]
    public async Task The_app_origin_is_the_apps_own_host_and_not_a_sibling()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        // A sibling's origin is a foreign origin, although it is same-site and the registry names it.
        BrowserResponse response = await browser.GetAsync(
            ApiPath, RequestOptions.With(("Origin", Harness.App("app2").Origin.GetLeftPart(UriPartial.Authority))), Suite.Ct);

        await AssertRefusedAsync(response, before, RuleIds.GateOrigin);
    }

    [Fact]
    public async Task A_same_origin_fetch_carrying_the_apps_own_origin_is_not_refused()
    {
        // The control. Without it every row above would pass against a gate that refused every
        // /api request, or every request carrying an Origin at all
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.GetAsync(
            ApiPath,
            RequestOptions.With(("Origin", Harness.App("app1").Origin.GetLeftPart(UriPartial.Authority))),
            Suite.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    [Fact]
    public async Task A_preflight_is_refused_and_grants_nothing()
    {
        // The method allowlist admits OPTIONS "for the APIs' own use", so a preflight reaches the
        // gate rather than stopping at stage 1; the gate then refuses it like any other cross-site
        // request, and nothing in the answer makes the browser's check pass.
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.FetchAsync(
            HttpMethod.Options,
            ApiPath,
            content: null,
            RequestOptions.With(
                ("Sec-Fetch-Site", FetchMetadata.CrossSite),
                ("Origin", "https://evil.test"),
                ("Access-Control-Request-Method", "POST"),
                ("Access-Control-Request-Headers", "x-csrf-token")),
            Suite.Ct);

        await AssertRefusedAsync(response, before, RuleIds.GateFetchSite);
        AssertNoCorsGrant(response);
    }

    [Fact]
    public async Task No_response_this_class_received_carries_an_access_control_header()
    {
        // The row's second sentence is about every response, not about the preflight alone, so it is
        // asserted over every exchange one browser made: refusals, the control's 200 from an
        // upstream, and the navigations in between.
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        await browser.GetAsync(ApiPath, ct: Suite.Ct);
        await browser.GetAsync(ApiPath, RequestOptions.With(("Sec-Fetch-Site", FetchMetadata.CrossSite)), Suite.Ct);
        await browser.FetchAsync(HttpMethod.Options, ApiPath, content: null, ct: Suite.Ct);
        await browser.GetAsync("/does-not-exist.js", ct: Suite.Ct);

        Assert.NotEmpty(browser.Exchanges);
        foreach (BrowserResponse exchange in browser.Exchanges)
        {
            AssertNoCorsGrant(exchange);
        }
    }

    private static void AssertNoCorsGrant(BrowserResponse response) =>
        Assert.DoesNotContain(
            response.Headers.Keys,
            name => name.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));

    private async Task AssertRefusedAsync(BrowserResponse response, int before, string rule)
    {
        Contract.ErrorRow expected = Contract.Error("The request gate refused the request");
        Assert.Equal(expected.Status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);

        Assert.Equal(rule, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
        AssertNoCorsGrant(response);
    }
}
