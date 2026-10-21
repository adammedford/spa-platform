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
///
/// <para>On the stack <see cref="StackSwitch"/> chooses. The sibling is a host under the app domain
/// that is no app, a compromised or hostile SPA (<see cref="IBrowserStack.Sibling"/>).</para>
/// </summary>
public sealed class OriginTests
{
    /// <summary>A site elsewhere.</summary>
    private static Uri Elsewhere => ForeignSites.Elsewhere;

    public static TheoryData<string, string> Initiators => new()
    {
        { "sibling", "same-site" },
        { "elsewhere", "cross-site" },
    };

    [Theory]
    [MemberData(nameof(Initiators))]
    public async Task N07_a_foreign_pages_calls_are_403_origin_rejected_with_no_cors_header_preflight_included(string initiator, string fetchSite)
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile victim = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await victim.SignInAsync(app);

        Uri page = initiator == "sibling" ? stack.Sibling : Elsewhere;
        await victim.OpenForeignPageAsync(page);
        int before = (await stack.ExchangesAsync()).Count;
        LogMark mark = stack.MarkLog();

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

        // The gate refused all three, the preflight among them: one line each, read before the raw
        // client below adds lines of its own.
        for (int refusal = 0; refusal < 3; refusal++)
        {
            await stack.AssertRuleLoggedAsync(mark, RuleIds.GateFetchSite, app.AppId);
        }

        Exchange[] sent = [.. (await stack.ExchangesAsync()).Skip(before)];
        foreach (string url in simple)
        {
            Exchange exchange = Assert.Single(sent, candidate => candidate.Url.ToString() == url);
            Assert.Equal(fetchSite, exchange.Sent("Sec-Fetch-Site"));

            // A sibling is same-site, so the browser sent the victim's session cookie with it; a site
            // elsewhere is cross-site, and SameSite=Lax kept the cookie back.
            bool cookieSent = (exchange.Sent("Cookie") ?? "").Contains(Contract.PlatformCookie("-session") + "=", StringComparison.Ordinal);
            Assert.Equal(fetchSite == "same-site", cookieSent);
            AssertOriginRejected(exchange, stack is LocalStack);
        }

        // The preflight is refused, so the POST itself is never sent.
        Assert.DoesNotContain(sent, candidate => candidate.Method == "POST");
        if (stack is LocalStack local)
        {
            Exchange preflight = Assert.Single(sent, candidate => candidate.Url == app.Url("/api/orders/echo"));
            Assert.Equal("OPTIONS", preflight.Method);
            AssertOriginRejected(preflight, bodyKept: true);
            Assert.Empty(local.Harness.Echo.Requests);
        }
        else
        {
            // The browser reports no preflight to a listener (BrowserExchangesTests), so on the cell the
            // raw client sends the one Chromium sent: its method, its Origin and its Fetch Metadata
            // (https://fetch.spec.whatwg.org/#cors-preflight-fetch).
            using HttpRequestMessage preflight = new(HttpMethod.Options, app.Url("/api/orders/echo"));
            preflight.Headers.Add("Origin", page.GetLeftPart(UriPartial.Authority));
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
            preflight.Headers.Add("Sec-Fetch-Site", fetchSite);
            preflight.Headers.Add("Sec-Fetch-Mode", "cors");
            preflight.Headers.Add("Sec-Fetch-Dest", "empty");
            using HttpResponseMessage refused = await stack.SendRawAsync(preflight);
            await AssertOriginRejectedAsync(refused);
        }
    }

    [Fact]
    public async Task N07_no_fetch_site_header_or_a_foreign_origin_is_403_origin_rejected_with_no_cors_header()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        HarnessApp app = stack.App("app1");

        LogMark mark = stack.MarkLog();
        foreach (string path in new[] { "/api/orders/whoami", "/bff/session" })
        {
            using HttpRequestMessage absent = new(HttpMethod.Get, app.Url(path));
            using HttpResponseMessage noHeader = await stack.SendRawAsync(absent);
            await AssertOriginRejectedAsync(noHeader);
            await stack.AssertRuleLoggedAsync(mark, RuleIds.GateFetchSite, app.AppId);

            using HttpRequestMessage foreign = new(HttpMethod.Get, app.Url(path));
            foreign.Headers.Add("Sec-Fetch-Site", "same-origin");
            foreign.Headers.Add("Origin", Elsewhere.GetLeftPart(UriPartial.Authority));
            using HttpResponseMessage foreignOrigin = await stack.SendRawAsync(foreign);
            await AssertOriginRejectedAsync(foreignOrigin);
            await stack.AssertRuleLoggedAsync(mark, RuleIds.GateOrigin, app.AppId);
        }
    }

    /// <summary>
    /// The refusal as the browser received it. The cell's record of a call CORS refused has no body,
    /// which the browser never read (<see cref="BrowserExchanges"/>); there the refusal is named by the
    /// rule in the log, <c>gate.fetch-site</c> (contracts/request-gate.md), and the raw client of the
    /// next test reads an <c>origin_rejected</c> body.
    /// </summary>
    private static void AssertOriginRejected(Exchange exchange, bool bodyKept)
    {
        Assert.Equal(Contract.Error("origin_rejected").Status, exchange.Status);
        if (bodyKept)
        {
            Assert.Contains($"\"code\":\"{Contract.Error("origin_rejected").Code}\"", exchange.BodyText, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(exchange.Headers, header => header.Key.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertOriginRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(Contract.Error("origin_rejected").Status, (int)response.StatusCode);
        Assert.Contains($"\"code\":\"{Contract.Error("origin_rejected").Code}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), header => header.Key.StartsWith(Contract.ForbiddenHeaderPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
