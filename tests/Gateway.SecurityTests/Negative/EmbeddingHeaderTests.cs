using System.Net;
using Gateway.TestHarness;
using Platform.Testing;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-24, whole for the response shapes that exist today: "Read framing and embedding headers on a
/// shell, an asset, a <c>/bff</c> response and an API error -> <c>frame-ancestors 'none'</c>,
/// <c>nosniff</c> and <c>Cross-Origin-Resource-Policy</c> on all four." (docs/plan/04-tests.md.)
///
/// <para><b>Three of the four shapes, and more besides.</b> A <c>/bff</c> response needs stage 10's
/// <c>MapPlatformBff</c>, which is workstream 3's and waits for <c>contracts-v1</c>; it joins this
/// class at G4. The other three are read here, and so are four shapes the row does not name but the
/// claim covers — the gateway's own 404, its 403 refusal, its 400 refusal and an upstream's 500 —
/// because the row behind this one is "there is no response shape that silently omits them", and a
/// refusal written by an early stage is exactly the shape most likely to.</para>
///
/// <para><b>Why that holds by construction, and why it is still worth asserting.</b> Stage 5 applies
/// the headers from <c>Response.OnStarting</c> rather than writing them as it runs, so they are
/// applied as the response begins — after every later stage has written its own, and after YARP has
/// copied an upstream's. The composed pipeline is the only place that claim can be read, because in
/// workstream 4's own project there is no stage 10 and no upstream to be overwritten.</para>
///
/// <para><b>Temporal caveat.</b> The shell and the asset are 200s only because the shell gate
/// (stage 8, workstream 3) is not in the pipeline yet. When it lands they become a challenge and a
/// 401 (ADR-0008). Stage 5 runs before stage 8, so the header assertions survive unchanged; it is
/// the 200s that are to be re-read.</para>
/// </summary>
public sealed class EmbeddingHeaderTests(EmbeddingHeaderTests.Cell cell) : IClassFixture<EmbeddingHeaderTests.Cell>
{
    /// <summary>One harness for this class, with the echo upstream arranged to fail on demand.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>
    /// One asset of the fixture SPA that is not the shell, read from <c>fixtures/spa/app1</c> rather
    /// than named here, so a renamed fixture file does not leave this reading the shell twice.
    /// </summary>
    private static string AssetPath =>
        "/" + FixtureFiles.Spa("app1").Keys
            .First(path => path.StartsWith("assets/", StringComparison.Ordinal));

    [Fact]
    public async Task Every_response_shape_carries_the_framing_sniffing_and_cross_origin_headers()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // The shell.
        BrowserResponse shell = await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);

        // An asset, which is a different endpoint path through the same stage.
        BrowserResponse asset = await browser.GetAsync(AssetPath, ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);

        // An API error, answered by the upstream rather than by the gateway: the mock API refuses an
        // unauthenticated caller with 401 invalid_token, and YARP copies its status and body back.
        BrowserResponse apiError = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, apiError.StatusCode);
        Assert.Contains("invalid_token", apiError.Body, StringComparison.Ordinal);

        // The gateway's own 404, for an asset the release does not hold.
        BrowserResponse missing = await browser.GetAsync("/nothing-here.js", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The gateway's own 403, written by stage 6 — the earliest shape that could omit them.
        BrowserResponse refused = await browser.GetAsync(
            "/api/echo/thing", RequestOptions.With(("Sec-Fetch-Site", FetchMetadata.CrossSite)), Suite.Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        foreach (BrowserResponse response in (BrowserResponse[])[shell, asset, apiError, missing, refused])
        {
            AssertEmbeddingHeaders(response);
        }
    }

    [Fact]
    public async Task A_400_refusal_written_before_routing_carries_them_too()
    {
        // Over the socket, because the path that produces this refusal is one no client will send
        // (a doubled slash). It is the shape with the fewest stages behind it that still reaches
        // stage 5.
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "//double", Harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(400, response.StatusCode);
        AssertEmbeddingHeaders(response.Header);
    }

    [Fact]
    public async Task An_upstreams_500_carries_them_as_a_200_does()
    {
        // The upstream decides the status; the gateway decides these headers. An upstream that fails
        // must not be able to serve a response the platform's headers are missing from.
        //
        // Its own harness, because arranging the upstream's answer changes state the whole class
        // would otherwise share: a row that needs a different cell takes its own rather than
        // mutating the fixture's.
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        harness.Echo.Respond = _ => new EchoResponse(HttpStatusCode.InternalServerError, "upstream failed");

        using TestBrowser browser = harness.CreateBrowser();
        await browser.NavigateAsync(harness.App("app1").Url("/"), ct: Suite.Ct);

        BrowserResponse failed = await browser.GetAsync("/api/echo/thing", ct: Suite.Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("upstream failed", failed.Body, StringComparison.Ordinal);
        AssertEmbeddingHeaders(failed);
    }

    [Fact]
    public async Task A_404_on_an_unknown_host_carries_them_only_if_an_app_was_resolved()
    {
        // The one shape that cannot carry an app's policy, and the reason this is a separate test
        // rather than a sixth row above: stage 5 reads the app stage 4 pinned, so a host the
        // registry does not name is refused before there is any policy to send. Reading that as a
        // gap in N-24 would be wrong — there is no app whose framing rules could apply, the body is
        // a fixed problem document, and ADR-0008's session requirement never arises. Asserted so
        // that the row's "on all four" is not quietly read as "on every response the gateway makes".
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", "nobody.spa-poc.test:8443", ct: Suite.Ct);

        Assert.Equal(404, response.StatusCode);
        Assert.Empty(response.Values("Content-Security-Policy"));
    }

    private static void AssertEmbeddingHeaders(BrowserResponse response) =>
        AssertEmbeddingHeaders(response.Header);

    /// <summary>
    /// The row's three headers, read from the contract rather than written down: the framing
    /// directive out of the baseline policy, and <c>nosniff</c> and
    /// <c>Cross-Origin-Resource-Policy</c> out of the fixed header list. The whole fixed list is
    /// checked while here, because a response that carried two of the three and dropped the rest
    /// would satisfy the row's words and not its reason.
    /// </summary>
    private static void AssertEmbeddingHeaders(Func<string, string?> header)
    {
        string? policy = header("Content-Security-Policy");
        Assert.NotNull(policy);

        KeyValuePair<string, string> framing = Assert.Single(
            Contract.Directives(policy).Where(directive => directive.Key == "frame-ancestors"));
        KeyValuePair<string, string> expected = Assert.Single(
            Contract.Directives(Contract.BaselineCspTemplate).Where(directive => directive.Key == "frame-ancestors"));
        Assert.Equal(expected.Value, framing.Value);

        // The row names the value and not just the directive (docs/plan/04-tests.md -> N-24), so the
        // baseline is held to it as well. Comparing the response against the contract alone would
        // admit a weakened baseline: both sides would move together and this row would still pass.
        Assert.Equal("'none'", expected.Value);

        foreach ((string name, string value) in Contract.FixedResponseHeaders)
        {
            Assert.Equal(value, header(name));
        }

        // Named explicitly, so the row's two other headers cannot pass by the list above happening
        // to be empty.
        Assert.Equal("nosniff", header("X-Content-Type-Options"));
        Assert.Equal("same-origin", header("Cross-Origin-Resource-Policy"));
    }
}
