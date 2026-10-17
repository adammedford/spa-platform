using System.Globalization;
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
/// <para>These requests isolate the HTTP contract named above. Session establishment and
/// preservation require separate acceptance evidence; a successful response here proves only the
/// asserted routing, policy or telemetry behaviour.</para>
/// </summary>
public sealed class AssetFallbackTests(AssetFallbackTests.Cell cell) : IClassFixture<AssetFallbackTests.Cell>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One harness for this class: nothing here changes the registry.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await GatewayHarness.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>One file of a fixture SPA, as the release in the harness's bucket holds it.</summary>
    private static byte[] Released(string appId, string path) => FixtureFiles.Spa(appId)[path];

    /// <summary>
    /// The script the fixture shell loads, by the path the shell names. Read out of the shell rather
    /// than written down here: the name carries a content hash and <c>fixtures/</c> is workstream 5's,
    /// so a rebuilt fixture renames it and a test holding the old name would fail for the wrong reason
    /// (CODING_STANDARDS.md -> "A guard reads the thing it guards").
    /// </summary>
    private static string ScriptPath(string appId) =>
        Encoding.UTF8.GetString(Released(appId, "index.html")).Split("src=\"")[1].Split('"')[0];

    [Fact]
    public async Task An_extensionless_path_that_accepts_html_returns_the_apps_own_shell()
    {
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

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
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);
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
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

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
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);

        // The script the fixture shell loads, by the path the shell names, with the type taken from
        // its extension (contracts/s3-layout.md) and not from what it was stored as.
        string script = ScriptPath("app1");
        BrowserResponse asset = await browser.NavigateAsync(Harness.App("app1").Url(script), ct: Ct);

        // The shell named an asset path, so the parse above found one rather than returning garbage.
        Assert.StartsWith("/assets/", script, StringComparison.Ordinal);
        Assert.EndsWith(".js", script, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/javascript", asset.Header("Content-Type"));
        Assert.Equal(Released("app1", script.TrimStart('/')), asset.Bytes);
    }

    [Fact]
    public async Task Head_answers_as_get_does_without_a_body()
    {
        // A raw socket, because this is a request no browser API sends and the Host header is what
        // routes it (AGENTS.md -> "Reach the gateway over a raw socket when the Host header matters").
        // With a signed-in browser's cookies, because an asset is served only in a session (ADR-0008).
        using TestBrowser browser = await Harness.CreateSignedInBrowserAsync(Ct);
        (string, string)[] session = [("Cookie", browser.Cookies.GetCookieHeader(Harness.App("app1").Origin))];
        string host = Harness.App("app1").Host;
        string script = ScriptPath("app1");
        RawResponse head = await RawHttp.SendAsync(Harness.GatewayAddress, "HEAD", script, host, session, Ct);
        RawResponse get = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", script, host, session, Ct);
        RawResponse headMissing = await RawHttp.SendAsync(Harness.GatewayAddress, "HEAD", "/assets/gone.js", host, session, Ct);

        // RFC 9110 section 9.3.2: the headers GET would send, Content-Length included, and no body. A
        // cache or a probe therefore sees the same decision the browser would.
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Header("Content-Length"), head.Header("Content-Length"));
        Assert.Equal(get.Header("Content-Type"), head.Header("Content-Type"));
        Assert.Equal(get.Header("ETag"), head.Header("ETag"));
        Assert.Equal(
            Released("app1", script.TrimStart('/')).Length.ToString(CultureInfo.InvariantCulture),
            head.Header("Content-Length"));
        Assert.Empty(head.Body);
        Assert.Equal(404, headMissing.StatusCode);
    }
}
