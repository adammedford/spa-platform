using System.Net;
using System.Text;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-06, second and third clauses: "request the fallback matrix" -> "Extensionless HTML paths return
/// <c>index.html</c>; a missing asset returns 404" (docs/plan/04-tests.md), through the composed
/// gateway. Task 6.2a.
///
/// <para>Every expected body is the bytes the release was published with, read from
/// <c>fixtures/spa/</c> through <see cref="FixtureFiles.Spa"/>, so a release served from another app's
/// prefix or a shell served in place of an asset is a failure rather than a coincidence.</para>
///
/// <para><b>Temporal caveat.</b> As in <see cref="PerAppHeadersTests"/>: stage 8, the shell gate, is
/// workstream 3's and is still a pass-through, so these are unauthenticated requests that reach the
/// asset endpoint. ADR-0008 requires a session before any asset, so when task 3.9 lands these become
/// authenticated navigations and the 200s below change <b>legitimately</b>. What the row is about —
/// which key the endpoint reads for which path — is the same either way.</para>
/// </summary>
public sealed class AssetFallbackTests(AssetFallbackTests.Cell cell) : IClassFixture<AssetFallbackTests.Cell>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One harness for this class: nothing here changes the registry.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Harness = await GatewayHarness.StartAsync();

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>One file of a fixture SPA, as the release in the harness's bucket holds it.</summary>
    private static byte[] Released(string appId, string path) => FixtureFiles.Spa(appId)[path];

    [Fact]
    public async Task An_extensionless_path_that_accepts_html_returns_the_apps_own_shell()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // A client-side route, as a full page load reaches it: no extension, and a browser's Accept.
        BrowserResponse route = await browser.NavigateAsync(Harness.App("app1").Url("/orders/42"), ct: Ct);
        BrowserResponse root = await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Ct);
        BrowserResponse neighbour = await browser.NavigateAsync(Harness.App("app2").Url("/orders/42"), ct: Ct);

        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        Assert.Equal("text/html", route.Header("Content-Type"));
        Assert.Equal(Released("app1", "index.html"), route.Bytes);
        Assert.Equal(Released("app1", "index.html"), root.Bytes);

        // The same path on the neighbour returns the neighbour's shell: the fallback resolves inside
        // the app's own release prefix (contracts/s3-layout.md), not to one shell for the gateway.
        Assert.Equal(Released("app2", "index.html"), neighbour.Bytes);
    }

    [Fact]
    public async Task An_extensionless_path_that_does_not_accept_html_is_not_rewritten()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app1").Url("/"), ct: Ct);

        // contracts/http-contract.md: the fallback is for "a path with no extension that accepts HTML".
        // A fetch that asks for JSON is not a page load, so it gets the 404 the key deserves.
        BrowserResponse json = await browser.GetAsync(
            Harness.App("app1").Url("/orders/42").ToString(), RequestOptions.With(("Accept", "application/json")), Ct);

        Assert.Equal(HttpStatusCode.NotFound, json.StatusCode);
        Assert.DoesNotContain("<!doctype html>", json.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_asset_returns_404_and_never_the_shell()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // A broken asset reference, and a missing page that carries an extension. Both have an
        // extension, so neither is rewritten, and a release that does not hold them answers 404.
        BrowserResponse missing = await browser.NavigateAsync(Harness.App("app1").Url("/assets/gone.js"), ct: Ct);
        BrowserResponse page = await browser.NavigateAsync(Harness.App("app1").Url("/orders.html"), ct: Ct);

        foreach (BrowserResponse response in (BrowserResponse[])[missing, page])
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.NotEqual(Released("app1", "index.html"), response.Bytes);
            Assert.DoesNotContain("<!doctype html>", response.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_path_with_an_extension_is_served_as_that_asset()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // The script the fixture shell loads, by the path the shell names, with the type taken from
        // its extension (contracts/s3-layout.md) and not from what it was stored as.
        string script = Encoding.UTF8.GetString(Released("app1", "index.html"))
            .Split("src=\"")[1].Split('"')[0];
        BrowserResponse asset = await browser.NavigateAsync(Harness.App("app1").Url(script), ct: Ct);

        Assert.Equal("/assets/app.43eda712.js", script);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/javascript", asset.Header("Content-Type"));
        Assert.Equal(Released("app1", script.TrimStart('/')), asset.Bytes);
    }

    [Fact]
    public async Task Head_answers_as_get_does_without_a_body()
    {
        // A raw socket, because this is a request no browser API sends and the Host header is what
        // routes it (AGENTS.md -> "Reach the gateway over a raw socket when the Host header matters").
        string host = Harness.App("app1").Host;
        RawResponse head = await RawHttp.SendAsync(Harness.GatewayAddress, "HEAD", "/assets/app.43eda712.js", host, ct: Ct);
        RawResponse get = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", "/assets/app.43eda712.js", host, ct: Ct);
        RawResponse headMissing = await RawHttp.SendAsync(Harness.GatewayAddress, "HEAD", "/assets/gone.js", host, ct: Ct);

        // RFC 9110 section 9.3.2: the headers GET would send, Content-Length included, and no body. A
        // cache or a probe therefore sees the same decision the browser would.
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Header("Content-Length"), head.Header("Content-Length"));
        Assert.Equal(get.Header("Content-Type"), head.Header("Content-Type"));
        Assert.Equal(get.Header("ETag"), head.Header("ETag"));
        Assert.Equal(Released("app1", "assets/app.43eda712.js").Length.ToString(System.Globalization.CultureInfo.InvariantCulture), head.Header("Content-Length"));
        Assert.Empty(head.Body);
        Assert.Equal(404, headMissing.StatusCode);
    }
}
