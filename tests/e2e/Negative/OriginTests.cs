using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-07 in a real browser (docs/plan/04-tests.md, tier D): <c>/api</c> and <c>/bff</c> called by a page
/// that is not the app. A hostile sibling under the app domain makes the browser send
/// <c>Sec-Fetch-Site: same-site</c>, and a site elsewhere <c>cross-site</c>; the browser computes the
/// header, so the test cannot get it wrong. An absent header and a foreign <c>Origin</c> beside
/// <c>same-origin</c> are requests no browser sends, so the raw client sends them.
///
/// <para>The victim is signed in. A sibling is same-site, so the browser attaches the victim's
/// <c>SameSite=Lax</c> session cookie to its requests: the gate, not <c>SameSite</c>, is what refuses
/// them (ADR-0007, D7).</para>
/// </summary>
public sealed class OriginTests
{
    /// <summary>A sibling host under the app domain that is no app: a compromised or hostile SPA.</summary>
    private static readonly Uri Sibling = new($"https://evil.{LocalStack.Domain}:8443/");

    /// <summary>A site elsewhere.</summary>
    private static readonly Uri Elsewhere = new($"https://{LocalStack.ForeignSite}/");

    public static TheoryData<string, string> Initiators => new()
    {
        { "sibling", "same-site" },
        { "elsewhere", "cross-site" },
    };

    [Theory]
    [MemberData(nameof(Initiators))]
    public async Task N07_a_foreign_pages_calls_are_403_origin_rejected_with_no_cors_header_preflight_included(string initiator, string fetchSite)
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile victim = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await victim.SignInAsync(app);

        await victim.OpenForeignPageAsync(initiator == "sibling" ? Sibling : Elsewhere);
        int before = stack.LoadBalancer.Exchanges.Count;
        int mark = stack.RequestLines.Count;

        // A simple request each, which the browser sends without a preflight, and one it must preflight.
        string[] simple = [app.Url("/api/orders/whoami").ToString(), app.Url("/bff/session").ToString()];
        foreach (string url in simple)
        {
            FetchResult result = await victim.FetchAsync(url, credentials: "include");
            Assert.NotNull(result.Error);
        }

        FetchResult preflighted = await victim.FetchAsync(
            app.Url("/api/orders/echo").ToString(), "POST", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "{}", "include");
        Assert.NotNull(preflighted.Error);

        Exchange[] sent = [.. stack.LoadBalancer.Exchanges.Skip(before)];
        foreach (string url in simple)
        {
            Exchange exchange = Assert.Single(sent, candidate => candidate.Url.ToString() == url);
            Assert.Equal(fetchSite, exchange.Sent("Sec-Fetch-Site"));

            // A sibling is same-site, so the browser sent the victim's session cookie with it; a site
            // elsewhere is cross-site, and SameSite=Lax kept the cookie back.
            bool cookieSent = (exchange.Sent("Cookie") ?? "").Contains(Contract.PlatformCookie("-session") + "=", StringComparison.Ordinal);
            Assert.Equal(fetchSite == "same-site", cookieSent);
            AssertOriginRejected(exchange);
        }

        // The preflight is refused, so the POST itself is never sent.
        Exchange preflight = Assert.Single(sent, candidate => candidate.Url == app.Url("/api/orders/echo"));
        Assert.Equal("OPTIONS", preflight.Method);
        AssertOriginRejected(preflight);

        await stack.AssertRuleLoggedAsync(mark, RuleIds.GateFetchSite, app.AppId);
        Assert.Empty(stack.Harness.Echo.Requests);
    }

    [Fact]
    public async Task N07_no_fetch_site_header_or_a_foreign_origin_is_403_origin_rejected_with_no_cors_header()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        HarnessApp app = stack.App("app1");

        foreach (string path in new[] { "/api/orders/whoami", "/bff/session" })
        {
            using HttpRequestMessage absent = new(HttpMethod.Get, app.Url(path));
            using HttpResponseMessage noHeader = await stack.SendRawAsync(absent);
            await AssertOriginRejectedAsync(noHeader);

            using HttpRequestMessage foreign = new(HttpMethod.Get, app.Url(path));
            foreign.Headers.Add("Sec-Fetch-Site", "same-origin");
            foreign.Headers.Add("Origin", Elsewhere.GetLeftPart(UriPartial.Authority));
            using HttpResponseMessage foreignOrigin = await stack.SendRawAsync(foreign);
            await AssertOriginRejectedAsync(foreignOrigin);
        }
    }

    private static void AssertOriginRejected(Exchange exchange)
    {
        Assert.Equal(403, exchange.Status);
        Assert.Contains($"\"code\":\"{Contract.Error("origin_rejected").Code}\"", exchange.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(exchange.Headers, header => header.Key.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertOriginRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Contains($"\"code\":\"{Contract.Error("origin_rejected").Code}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), header => header.Key.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
