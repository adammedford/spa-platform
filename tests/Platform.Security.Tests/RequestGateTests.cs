using System.Net;
using System.Text.Json;

namespace Platform.Security.Tests;

/// <summary>
/// Stage 6, the request gate: the rules in <c>contracts/request-gate.md</c>, each refusal under its
/// rule id, with the codes in <c>contracts/http-contract.md</c> -> Errors. Task 4.3; N-07, N-25, N-27,
/// and the escape paths of N-10.
/// </summary>
public sealed class RequestGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string, string?) FetchSite(string? value) => ("Sec-Fetch-Site", value);

    private static (string, string?) Origin(string? value) => ("Origin", value);

    private static async Task AssertRefusedAsync(
        GateHost host, HttpResponseMessage response, HttpStatusCode status, string code, string rule)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal((int)status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());

        // N-07: no Access-Control-Allow-* on a refusal. The terminal sends one, so this fails if the
        // request got past the gate.
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, host.Reached);
        Assert.Single(host.LinesFor(rule));
    }

    // ---- N-07: /api and /bff take same-origin requests only ----

    [Theory]
    [InlineData("GET", "/api/orders/items", "cross-site")]
    [InlineData("GET", "/api/orders/items", "same-site")]
    [InlineData("GET", "/api/orders/items", "none")]
    [InlineData("GET", "/api/orders/items", null)]
    [InlineData("POST", "/api/orders/items", "same-site")]
    [InlineData("OPTIONS", "/api/orders/items", "cross-site")]
    [InlineData("GET", "/bff/session", "cross-site")]
    [InlineData("GET", "/bff/session", "same-site")]
    [InlineData("GET", "/bff/session", null)]
    [InlineData("GET", "/bff/login", "cross-site")]
    [InlineData("GET", "/bff/login", "same-site")]
    [InlineData("GET", "/bff/login", null)]
    [InlineData("POST", "/bff/logout", "cross-site")]
    [InlineData("POST", "/bff/logout", "same-site")]
    [InlineData("POST", "/bff/logout", null)]
    public async Task N07_a_request_not_from_the_app_itself_gets_403_origin_rejected(
        string method, string path, string? fetchSite)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite(fetchSite), Origin(GateHost.AppOrigin),
            ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.fetch-site");
    }

    private static async Task AssertPassedAsync(GateHost host, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GateHost.Passed, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, host.Reached);
    }

    [Theory]
    [InlineData("GET", "/api/orders/items")]
    [InlineData("POST", "/api/orders/items")]
    [InlineData("DELETE", "/api/orders/items/7")]
    [InlineData("GET", "/bff/session")]
    [InlineData("POST", "/bff/logout")]
    public async Task A_same_origin_request_from_the_app_passes(string method, string path)
    {
        // The other half of N-07: the same headers with the app's own values pass, so each refusal
        // above is the gate's judgement on the value that changed.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin));

        await AssertPassedAsync(host, response);
    }

    [Fact]
    public async Task An_api_request_with_no_origin_passes()
    {
        // "Origin, when sent": a same-origin GET from fetch() carries none.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/api/orders/items", Ct, FetchSite("same-origin"));

        await AssertPassedAsync(host, response);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task Login_as_a_document_navigation_passes(string fetchSite)
    {
        // none is a navigation the user started: a bookmark, or the address bar.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/login?returnUrl=/orders", Ct,
            FetchSite(fetchSite), ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"));

        await AssertPassedAsync(host, response);
    }

    [Theory]
    [InlineData("GET", "/api/orders/items")]
    [InlineData("POST", "/api/orders/items")]
    [InlineData("OPTIONS", "/api/orders/items")]
    [InlineData("GET", "/bff/session")]
    [InlineData("POST", "/bff/logout")]
    public async Task N07_a_foreign_origin_gets_403_origin_rejected(string method, string path)
    {
        // A sibling SPA's origin, with Sec-Fetch-Site claiming same-origin: no browser sends that pair,
        // so this is the Origin check on its own.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.SiblingOrigin));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.origin");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("http://app1.spa-poc.test")]
    [InlineData("https://app1.spa-poc.test:8443")]
    [InlineData("https://evil.example")]
    public async Task N07_an_origin_that_is_not_exactly_the_app_origin_is_refused(string origin)
    {
        // The scheme counts: behind the ALB the app origin is https, and an http page is another origin.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", Ct,
            FetchSite("same-origin"), Origin(origin));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.origin");
    }

    [Fact]
    public async Task Logout_without_an_origin_is_refused()
    {
        // A form post carries no token, so the Origin is the whole of the check (contract, /bff/logout).
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/bff/logout", Ct, FetchSite("same-origin"));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.origin");
    }

    [Theory]
    [InlineData("no-cors", "image")]
    [InlineData("cors", "empty")]
    [InlineData("navigate", "iframe")]
    [InlineData(null, null)]
    public async Task Login_that_is_not_a_document_navigation_is_refused(string? mode, string? dest)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/login", Ct,
            FetchSite("same-origin"), ("Sec-Fetch-Mode", mode), ("Sec-Fetch-Dest", dest));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.navigation");
    }

    [Theory]
    [InlineData("POST", "/bff/session", "GET")]
    [InlineData("DELETE", "/bff/session", "GET")]
    [InlineData("POST", "/bff/login", "GET")]
    [InlineData("GET", "/bff/logout", "POST")]
    [InlineData("POST", "/signin-oidc", "GET")]
    [InlineData("POST", "/signout-callback-oidc", "GET")]
    [InlineData("POST", "/index.html", "GET, HEAD")]
    [InlineData("PUT", "/", "GET, HEAD")]
    [InlineData("DELETE", "/orders/7", "GET, HEAD")]
    public async Task A_method_the_path_does_not_take_gets_405(string method, string path, string allow)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin),
            ("Sec-Fetch-Mode", "navigate"), ("Sec-Fetch-Dest", "document"));

        await AssertRefusedAsync(host, response, HttpStatusCode.MethodNotAllowed, "method_not_allowed", "gate.method");
        Assert.Equal(allow, string.Join(", ", response.Content.Headers.Allow));
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("HEAD", "/assets/app-3f9a.js")]
    [InlineData("GET", "/orders/7")]
    [InlineData("GET", "/signin-oidc?code=c&state=s")]
    [InlineData("GET", "/signout-callback-oidc")]
    public async Task Assets_and_callbacks_pass_from_any_site(string method, string path)
    {
        // Sec-Fetch-Site: Any. A navigation from another site, the provider's redirect included, lands
        // here, and the session these need is the shell gate's to judge (stage 8).
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct, FetchSite("cross-site"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Reached);
    }

    [Theory]
    [InlineData("/BFF/session")]
    [InlineData("/Bff/Logout")]
    [InlineData("/API/orders/items")]
    [InlineData("/bff/session/")]
    public async Task A_platform_path_in_another_letter_case_is_judged_as_that_path(string path)
    {
        // Endpoint routing matches without regard to case, so the gate must too, or /BFF/session would
        // reach the session endpoint as an asset request, past the Sec-Fetch-Site check.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", path, Ct, FetchSite("cross-site"));

        await AssertRefusedAsync(host, response, HttpStatusCode.Forbidden, "origin_rejected", "gate.fetch-site");
    }

    // ---- N-27: the framework's remote sign-out is switched off ----

    [Theory]
    [InlineData("GET", "/signout-oidc")]
    [InlineData("POST", "/signout-oidc")]
    [InlineData("GET", "/signout-oidc?sid=abc&iss=https%3A%2F%2Fidp.example")]
    [InlineData("GET", "/SIGNOUT-OIDC")]
    [InlineData("GET", "/bff/other")]
    [InlineData("POST", "/bff")]
    public async Task N27_signout_oidc_gets_404_and_nothing_after_the_gate_runs(string method, string path)
    {
        // The session survives because the request never reaches authentication, where the handler
        // would sign out: no stage after the gate runs, and the 404 sets no cookie. The terminal sends
        // a Set-Cookie that clears the session, so a request let through would show one. Workstream 6
        // proves the live session survives in the composed suite.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin), ("Cookie", "__Host-spa-session=live"));

        await AssertRefusedAsync(host, response, HttpStatusCode.NotFound, "not_found", "path.reserved");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // ---- N-25: a cookie name that imitates a platform cookie ----

    [Theory]
    [InlineData("__host-spa-session=planted")]
    [InlineData("__HOST-SPA-SESSION=planted")]
    [InlineData("__Host-SPA-session=planted")]
    [InlineData("__host-spa-csrf=planted")]
    [InlineData("__Host-Spa-corr.abc=planted")]
    [InlineData("__Host-spa-session=live; __host-spa-session=planted")]
    [InlineData("theme=dark;__host-spa-nonce.xyz=planted")]
    [InlineData("__Host-spa-session=live; __Host-spa-session=planted")]
    [InlineData("__Host-spa-session=live; __Host-spa-SESSION=planted")]
    public async Task N25_a_case_variant_or_a_second_copy_of_a_platform_cookie_gets_400(string cookie)
    {
        // On an asset path, where no other rule applies: the cookie rule covers every path.
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/", Ct, ("Cookie", cookie));

        await AssertRefusedAsync(host, response, HttpStatusCode.BadRequest, "invalid_request", "gate.cookie-name");
        lock (host.Log)
        {
            Assert.DoesNotContain(host.Log, line => line.Contains("planted", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task N25_a_case_variant_in_a_second_cookie_header_is_refused()
    {
        // HTTP/2 lets a client split Cookie across header fields; the check reads every one.
        await using GateHost host = await GateHost.StartAsync(Ct);
        string response = await host.RawAsync("GET / HTTP/1.1", Ct,
            "Cookie: __Host-spa-session=live\r\nCookie: __HOST-spa-session=planted\r\n");

        Assert.StartsWith("HTTP/1.1 400 ", response);
        Assert.Single(host.LinesFor("gate.cookie-name"));
    }

    [Theory]
    [InlineData("__Host-spa-session=live")]
    [InlineData("__Host-spa-session=live; __Host-spa-csrf=t; __Host-spa-corr.abc=c; __Host-spa-nonce.abc=n")]
    [InlineData("__Host-spa-sessionC1=a; __Host-spa-sessionC2=b; __Host-spa-session=chunks-2")]
    [InlineData("theme=dark; host-spa-session=x; spa-session=y")]
    public async Task Platform_cookies_under_their_own_names_pass(string cookie)
    {
        await using GateHost host = await GateHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/", Ct, ("Cookie", cookie));

        await AssertPassedAsync(host, response);
    }

    // ---- N-10's escape paths: a path that failed normalisation ----

    [Theory]
    [InlineData("/api/orders/%2e%2e%2fsecret")]
    [InlineData("/api/orders/%2E%2E%2Fsecret")]
    [InlineData("/api/orders/%2e%2e%5csecret")]
    [InlineData("/api/orders/..;/secret")]
    [InlineData("/api/orders//../secret")]
    [InlineData("/api/orders/../reports/x")]
    [InlineData("/api/orders/./x")]
    [InlineData("/api//orders/x")]
    [InlineData("/api/orders/a%2fb")]
    [InlineData("/api/orders\\..\\secret")]
    [InlineData("/assets/..;jsessionid=1/index.html")]
    [InlineData("http://{authority}/api/orders/x")]
    public async Task N10_a_path_built_to_escape_its_prefix_gets_400_invalid_request(string target)
    {
        // Finding T-C10: YARP forwards each of the first five to the app's own upstream as sent. These
        // go over a socket, because Uri would resolve the dot segments before the request left. The last is
        // the absolute form with the authority Kestrel requires to match Host: legal HTTP that no browser
        // sends an origin server.
        await using GateHost host = await GateHost.StartAsync(Ct);

        string response = await host.RawAsync(
            $"GET {target.Replace("{authority}", host.BaseAddress.Authority, StringComparison.Ordinal)} HTTP/1.1", Ct,
            "Sec-Fetch-Site: same-origin\r\n");

        Assert.StartsWith("HTTP/1.1 400 ", response);
        Assert.Contains("\"code\":\"invalid_request\"", response);
        Assert.Equal(0, host.Reached);
        Assert.Single(host.LinesFor("path.invalid"));
    }

    [Theory]
    [InlineData("/api/orders/items?q=a/../b")]
    [InlineData("/api/orders/items/")]
    [InlineData("/api/orders/a%20b")]
    [InlineData("/api/orders/v1.2/items")]
    [InlineData("/")]
    public async Task A_normal_path_passes_the_path_check(string target)
    {
        // The query is not part of the path, a trailing slash is not a doubled one, and a dot inside a
        // segment is not a dot segment.
        await using GateHost host = await GateHost.StartAsync(Ct);

        string response = await host.RawAsync($"GET {target} HTTP/1.1", Ct, "Sec-Fetch-Site: same-origin\r\n");

        Assert.StartsWith("HTTP/1.1 200 ", response);
        Assert.Equal(1, host.Reached);
    }

    // ---- The stage it stands on ----

    [Fact]
    public async Task Without_host_resolution_the_gate_fails_closed()
    {
        // No IAppFeature means stage 4 did not run, and there is no app origin to judge by. The gate
        // throws rather than pass the request on, so nothing after it runs.
        await using GateHost host = await GateHost.StartAsync(Ct, pinApp: false);

        using HttpResponseMessage response = await host.SendAsync("GET", "/", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, host.Reached);
    }
}
