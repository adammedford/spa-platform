using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-07: "Call <c>/api</c> and <c>/bff</c> with <c>Sec-Fetch-Site</c> of
/// <c>cross-site</c>, <c>same-site</c> or absent, or a foreign <c>Origin</c> -> 403
/// <c>origin_rejected</c>. No <c>Access-Control-Allow-*</c> header on any response, including
/// preflight." (docs/plan/04-tests.md.)
///
/// <para>The class fixture's requests prove the request gate's refusals on API and BFF paths, before
/// authentication (contracts/pipeline.md), with no session: each browser is put on the app's page
/// through <c>/healthz</c>, because a navigation to the shell would sign it in.
/// <see cref="A_signed_in_browser_is_refused_the_same_way_and_its_successful_answers_grant_no_cross_origin_read"/>
/// repeats them for a browser holding a live session, on a cell of its own, and reads the
/// successful <c>/bff/session</c> and <c>/api</c> answers for the row's "on any response".</para>
///
/// <para><b>The two refusals are told apart.</b> Both answer 403 <c>origin_rejected</c>, because the
/// <c>code</c> "says little on purpose"; the rule id is what distinguishes them
/// (<see cref="RuleIds.GateFetchSite"/> from <see cref="RuleIds.GateOrigin"/>), and the contract
/// says tests assert on both. A test that read only the status could not tell a gate that lost its
/// <c>Origin</c> check from one that kept it.</para>
///
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
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
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
        await SignedIn.OnPageOfAsync(browser, Harness.App("app2"));
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
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
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
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
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
        // (CODING_STANDARDS.md -> "An assertion must be able to fail"). The gate admits it; what
        // answers is the proxy's token step, because the call carries no session: 401
        // unauthenticated under no rule id, where each refusal above is a 403 under the gate's rule
        // (contracts/http-contract.md -> Errors).
        using TestBrowser browser = Harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.GetAsync(
            ApiPath,
            RequestOptions.With(("Origin", Harness.App("app1").Origin.GetLeftPart(UriPartial.Authority))),
            Suite.Ct);

        Contract.ErrorRow noSession = Contract.Error("unauthenticated");
        Assert.Equal((HttpStatusCode)noSession.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{noSession.Code}\"", response.Body, StringComparison.Ordinal);
        Assert.Null(await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    [Fact]
    public async Task A_preflight_is_refused_and_grants_nothing()
    {
        // The method allowlist admits OPTIONS "for the APIs' own use", so a preflight reaches the
        // gate rather than stopping at the allowlist; the gate then refuses it like any other cross-site
        // request, and nothing in the answer makes the browser's check pass.
        using TestBrowser browser = Harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
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
        // asserted over every exchange one browser made: the page, the gate's refusals, and the 401s
        // that an admitted API call and an asset fetch get with no session. The successful answers
        // are the signed-in row's, below.
        using TestBrowser browser = Harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));
        BrowserResponse admitted = await browser.GetAsync(ApiPath, ct: Suite.Ct);
        await browser.GetAsync(ApiPath, RequestOptions.With(("Sec-Fetch-Site", FetchMetadata.CrossSite)), Suite.Ct);
        await browser.FetchAsync(HttpMethod.Options, ApiPath, content: null, ct: Suite.Ct);
        BrowserResponse asset = await browser.GetAsync("/does-not-exist.js", ct: Suite.Ct);

        // The two that pass the gate are refused for want of a session, so the sweep covers the shapes it names.
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, admitted.StatusCode);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, asset.StatusCode);
        Assert.NotEmpty(browser.Exchanges);
        foreach (BrowserResponse exchange in browser.Exchanges)
        {
            AssertNoCorsGrant(exchange);
        }
    }

    [Fact]
    public async Task A_signed_in_browser_is_refused_the_same_way_and_its_successful_answers_grant_no_cross_origin_read()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);

        // The controls: with the session, both paths answer.
        BrowserResponse session = await browser.GetAsync(BffPath, ct: Suite.Ct);
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        // The session cookie travels on every refused request below, so the refusal is the gate's and
        // a session does not buy past it.
        foreach (string path in (string[])[BffPath, ApiPath])
        {
            foreach (string? site in (string?[])[FetchMetadata.CrossSite, FetchMetadata.SameSite, null])
            {
                int before = await Observed.SettledCountAsync(harness, Suite.Ct);
                BrowserResponse refused = await browser.GetAsync(path, RequestOptions.With(("Sec-Fetch-Site", site)), Suite.Ct);
                Assert.Contains(SignedIn.SessionCookieName + "=", refused.Sent("Cookie"), StringComparison.Ordinal);
                await AssertRefusedAsync(harness, refused, before, RuleIds.GateFetchSite);
            }

            int count = await Observed.SettledCountAsync(harness, Suite.Ct);
            BrowserResponse foreign = await browser.GetAsync(path, RequestOptions.With(("Origin", "https://evil.test")), Suite.Ct);
            await AssertRefusedAsync(harness, foreign, count, RuleIds.GateOrigin);
        }

        Assert.NotEmpty(browser.Exchanges);
        foreach (BrowserResponse exchange in browser.Exchanges)
        {
            AssertNoCorsGrant(exchange);
        }

        Assert.True(SignedIn.HoldsSessionCookie(browser, app));
    }

    private static void AssertNoCorsGrant(BrowserResponse response) =>
        Assert.DoesNotContain(
            response.Headers.Keys,
            name => name.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));

    private Task AssertRefusedAsync(BrowserResponse response, int before, string rule) =>
        AssertRefusedAsync(Harness, response, before, rule);

    private static async Task AssertRefusedAsync(GatewayHarness harness, BrowserResponse response, int before, string rule)
    {
        Contract.ErrorRow expected = Contract.Error("origin_rejected");
        Assert.Equal(expected.Status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);

        Assert.Equal(rule, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        AssertNoCorsGrant(response);
    }
}
