using System.Collections.Concurrent;
using System.Net;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// The test browser against a scripted network, so each rule it implements can be shown on its own:
/// cookies per host, and the Fetch Metadata and <c>Origin</c> a current browser would send.
/// </summary>
public sealed class TestBrowserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri App1 = new("https://app1.spa-poc.test:8443/");
    private static readonly Uri App2 = new("https://app2.spa-poc.test:8443/");
    private static readonly Uri Idp = new("http://127.0.0.1:7001/");

    [Fact]
    public async Task A_host_only_cookie_stays_with_its_host_and_a_domain_cookie_reaches_every_sibling()
    {
        Network network = new();
        network.On(App1, "/", _ => Respond(HttpStatusCode.OK,
            ("Set-Cookie", "__Host-spa-session=one; Path=/; Secure; HttpOnly"),
            ("Set-Cookie", "planted=parent; Domain=spa-poc.test; Path=/; Secure")));
        using TestBrowser browser = new(network);

        await browser.NavigateAsync(App1, ct: Ct);
        BrowserResponse sameHost = await browser.NavigateAsync(new Uri(App1, "/again"), ct: Ct);
        BrowserResponse sibling = await browser.NavigateAsync(App2, ct: Ct);

        Assert.Contains("__Host-spa-session=one", sameHost.Sent("Cookie"));
        Assert.Contains("planted=parent", sameHost.Sent("Cookie"));
        Assert.DoesNotContain("__Host-spa-session", sibling.Sent("Cookie") ?? "");
        Assert.Contains("planted=parent", sibling.Sent("Cookie"));
    }

    [Fact]
    public async Task A_secure_cookie_is_not_sent_over_plain_http()
    {
        Network network = new();
        network.On(App1, "/", _ => Respond(HttpStatusCode.OK, ("Set-Cookie", "s=1; Path=/; Secure")));
        using TestBrowser browser = new(network);

        await browser.NavigateAsync(App1, ct: Ct);
        BrowserResponse plain = await browser.NavigateAsync("http://app1.spa-poc.test:8443/", ct: Ct);

        Assert.Null(plain.Sent("Cookie"));
    }

    [Fact]
    public async Task A_typed_navigation_is_none_and_a_login_that_went_through_the_provider_returns_cross_site()
    {
        Network network = new();
        network.On(App1, "/bff/login", _ => Respond(HttpStatusCode.Found, ("Location", new Uri(Idp, "/authorize").ToString())));
        network.On(Idp, "/authorize", _ => Respond(HttpStatusCode.Found, ("Location", new Uri(App1, "/signin-oidc?code=c").ToString())));
        using TestBrowser browser = new(network);

        BrowserResponse typed = await browser.NavigateAsync(App1, ct: Ct);
        BrowserResponse callback = await browser.ClickAsync("/bff/login", ct: Ct);

        Assert.Equal(FetchMetadata.None, typed.Sent("Sec-Fetch-Site"));
        Assert.Equal("navigate", typed.Sent("Sec-Fetch-Mode"));
        Assert.Equal("document", typed.Sent("Sec-Fetch-Dest"));

        BrowserResponse[] hops = [.. browser.Exchanges.Skip(1)];
        Assert.Equal(FetchMetadata.SameOrigin, hops[0].Sent("Sec-Fetch-Site"));
        Assert.Equal(FetchMetadata.CrossSite, hops[1].Sent("Sec-Fetch-Site"));
        Assert.Equal(FetchMetadata.CrossSite, callback.Sent("Sec-Fetch-Site"));
        Assert.Equal(new Uri(App1, "/signin-oidc?code=c"), callback.Url);
        Assert.Equal(callback.Url, browser.Page);
        Assert.Equal(2, callback.RedirectedFrom.Length);
    }

    [Fact]
    public async Task A_fetch_is_same_origin_on_its_own_host_and_same_site_on_a_sibling_and_carries_origin_when_a_browser_would()
    {
        Network network = new();
        using TestBrowser browser = new(network);
        await browser.NavigateAsync(App1, ct: Ct);

        BrowserResponse own = await browser.GetAsync("/api/orders/whoami", ct: Ct);
        BrowserResponse post = await browser.FetchAsync(HttpMethod.Post, "/api/orders/echo", new StringContent("{}"), ct: Ct);
        BrowserResponse sibling = await browser.GetAsync(new Uri(App2, "/api/orders/whoami").ToString(), ct: Ct);

        Assert.Equal(FetchMetadata.SameOrigin, own.Sent("Sec-Fetch-Site"));
        Assert.Equal("cors", own.Sent("Sec-Fetch-Mode"));
        Assert.Equal("empty", own.Sent("Sec-Fetch-Dest"));
        Assert.Null(own.Sent("Origin"));
        Assert.Equal("https://app1.spa-poc.test:8443", post.Sent("Origin"));
        Assert.Equal(FetchMetadata.SameSite, sibling.Sent("Sec-Fetch-Site"));
        Assert.Equal("https://app1.spa-poc.test:8443", sibling.Sent("Origin"));
    }

    [Fact]
    public async Task A_post_form_sends_origin_and_a_303_turns_it_into_a_get_without_one()
    {
        Network network = new();
        network.On(App1, "/bff/logout", _ => Respond(HttpStatusCode.SeeOther, ("Location", "/")));
        using TestBrowser browser = new(network);
        await browser.NavigateAsync(App1, ct: Ct);

        BrowserResponse landed = await browser.SubmitFormAsync("/bff/logout", ct: Ct);

        BrowserResponse logout = browser.Exchanges[^2];
        Assert.Equal("POST", logout.Method);
        Assert.Equal("https://app1.spa-poc.test:8443", logout.Sent("Origin"));
        Assert.Equal(FetchMetadata.SameOrigin, logout.Sent("Sec-Fetch-Site"));
        Assert.Equal("GET", landed.Method);
        Assert.Null(landed.Sent("Origin"));
    }

    [Fact]
    public async Task A_test_overrides_or_removes_a_header_the_browser_would_send()
    {
        Network network = new();
        using TestBrowser browser = new(network);
        await browser.NavigateAsync(App1, ct: Ct);

        BrowserResponse absent = await browser.GetAsync("/api/orders/whoami", RequestOptions.With(("Sec-Fetch-Site", null)), Ct);
        BrowserResponse forged = await browser.GetAsync("/api/orders/whoami", RequestOptions.With(("Origin", "https://evil.test")), Ct);

        Assert.Null(absent.Sent("Sec-Fetch-Site"));
        Assert.False(network.Received[1].Headers.ContainsKey("Sec-Fetch-Site"));
        Assert.Equal("https://evil.test", forged.Sent("Origin"));
    }

    [Fact]
    public async Task A_fetch_does_not_follow_a_redirect_off_its_origin_and_a_manual_navigation_follows_none()
    {
        Network network = new();
        network.On(App1, "/api/x", _ => Respond(HttpStatusCode.Found, ("Location", new Uri(Idp, "/authorize").ToString())));
        network.On(App1, "/go", _ => Respond(HttpStatusCode.Found, ("Location", "/elsewhere")));
        using TestBrowser browser = new(network);
        await browser.NavigateAsync(App1, ct: Ct);

        BrowserResponse fetched = await browser.GetAsync("/api/x", ct: Ct);
        BrowserResponse manual = await browser.ClickAsync("/go", new RequestOptions { Redirect = RedirectMode.Manual }, Ct);

        Assert.Equal(HttpStatusCode.Found, fetched.StatusCode);
        Assert.Equal(HttpStatusCode.Found, manual.StatusCode);
        Assert.DoesNotContain(network.Received, r => r.Url.AbsolutePath is "/authorize" or "/elsewhere");
    }

    [Theory]
    [InlineData("https://app1.spa-poc.test:8443/", "https://app1.spa-poc.test:8443/x", FetchMetadata.SameOrigin)]
    [InlineData("https://app1.spa-poc.test:8443/", "https://app2.spa-poc.test:8443/x", FetchMetadata.SameSite)]
    [InlineData("https://app1.spa-poc.test:8443/", "https://app1.spa-poc.test:9443/x", FetchMetadata.SameSite)]
    [InlineData("https://app1.spa-poc.test:8443/", "http://app1.spa-poc.test:8443/x", FetchMetadata.CrossSite)]
    [InlineData("https://app1.spa-poc.test:8443/", "https://evil.test/x", FetchMetadata.CrossSite)]
    [InlineData("http://127.0.0.1:7001/", "http://127.0.0.2:7001/x", FetchMetadata.CrossSite)]
    public void Site_follows_the_specification(string initiator, string target, string expected) =>
        Assert.Equal(expected, FetchMetadata.Site(new Uri(initiator), [new Uri(target)]));

    private static HttpResponseMessage Respond(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent("") };
        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    /// <summary>A network that answers from a script, 200 for anything unscripted, and records what it was sent.</summary>
    private sealed class Network : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes = new();
        private readonly List<(Uri Url, Dictionary<string, string> Headers)> received = [];

        public IReadOnlyList<(Uri Url, Dictionary<string, string> Headers)> Received => received;

        public void On(Uri origin, string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            routes[origin.GetLeftPart(UriPartial.Authority) + path] = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri url = request.RequestUri!;
            received.Add((url, request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(routes.TryGetValue(url.GetLeftPart(UriPartial.Path), out var respond)
                ? respond(request)
                : Respond(HttpStatusCode.OK));
        }
    }
}
