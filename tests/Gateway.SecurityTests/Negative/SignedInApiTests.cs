using System.Net;
using Gateway.TestHarness;
using Platform.Testing;
using TestIdp;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The <c>/api</c> rows that need a session: N-11, N-17 and N-19 (docs/plan/04-tests.md).
///
/// <list type="bullet">
/// <item>N-11: "Send <c>Cookie</c>, a bearer token, the CSRF header and forwarding headers on an API
/// call -> The upstream sees none of them. It sees the session's token, and the three forwarding
/// headers as the gateway set them."</item>
/// <item>N-17: "Send app 1's token to the <c>reports</c> API and app 2's to <c>orders</c>, through the
/// second upstream each app lists -> The mock API refuses both: wrong client, and on <c>orders</c> a
/// missing audience."</item>
/// <item>N-19: "Unauthenticated call to <c>/api</c> and <c>/bff/session</c> -> 401 with a problem
/// body, never a redirect."</item>
/// </list>
///
/// <para>Workstream 4's own suite proves N-11 against a stand-in token source (task 4.2). Here the
/// token is the one the test provider issued to this session, through the real token source, which is
/// the claim the row makes.</para>
/// </summary>
public sealed class SignedInApiTests
{
    [Fact]
    public async Task N11_the_upstream_sees_the_sessions_token_and_the_gateways_forwarding_headers_and_nothing_the_caller_planted()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string sessionToken = Assert.IsType<string>(
            harness.Idp.Grants.Last(grant => grant.Ok && grant.ClientId == app.ClientId).AccessToken);

        BrowserResponse response = await browser.GetAsync("/api/echo/thing", RequestOptions.With(
            ("Authorization", "Bearer " + Canaries.Token),
            (Contract.CsrfHeaderName, Canaries.Csrf),
            ("X-Forwarded-Host", "evil.test"),
            ("X-Forwarded-Proto", "http"),
            ("X-Forwarded-For", "203.0.113.66"),
            ("Forwarded", "for=203.0.113.66;host=evil.test;proto=http")), Suite.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The browser really sent what the upstream must not see, so the absences below can fail.
        Assert.Contains(SignedIn.SessionCookieName + "=", response.Sent("Cookie"), StringComparison.Ordinal);
        Assert.Equal("Bearer " + Canaries.Token, response.Sent("Authorization"));

        EchoedRequest seen = harness.Echo.Requests[^1];
        Assert.Equal("/thing", seen.Path);
        Assert.False(seen.Headers.ContainsKey("Cookie"));
        Assert.False(seen.Headers.ContainsKey(Contract.CsrfHeaderName));
        Assert.False(seen.Headers.ContainsKey("Forwarded"));

        // The session's token, and only it, labelled rather than printed on failure.
        string authorization = Assert.Single(seen.Headers["Authorization"]);
        Assert.True(authorization == "Bearer " + sessionToken, $"the upstream saw {TestIdentityProvider.Label(authorization)}");

        // The three forwarding headers, each as the gateway set it.
        Assert.Equal(app.Host, Assert.Single(seen.Headers["X-Forwarded-Host"]));
        Assert.Equal("https", Assert.Single(seen.Headers["X-Forwarded-Proto"]));
        string forwardedFor = Assert.Single(seen.Headers["X-Forwarded-For"]);
        Assert.DoesNotContain("203.0.113.66", forwardedFor, StringComparison.Ordinal);
        Assert.Empty(seen.Headers.Keys.Where(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
            .Except(["X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-For"], StringComparer.OrdinalIgnoreCase));

        // Nothing the caller planted, under any header name.
        string all = string.Join("\n", seen.Headers.SelectMany(header => header.Value.Select(value => header.Key + ": " + value)));
        foreach (string planted in (string[])[Canaries.Token, Canaries.Csrf, "evil.test", SignedIn.SessionCookie(browser, app)])
        {
            Assert.DoesNotContain(planted, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task N17_each_apps_token_is_refused_by_the_api_it_was_not_issued_for()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = harness.CreateBrowser();

        // Through the second upstream each app lists, read from the fixture rather than named here.
        string app1Second = SecondApi("app1"), app2Second = SecondApi("app2");
        Assert.Equal("reports", app1Second);
        Assert.Equal("orders", app2Second);

        await harness.SignInAsync(browser, "app1", Suite.Ct);
        BrowserResponse app1Own = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        BrowserResponse app1OnReports = await browser.GetAsync($"/api/{app1Second}/whoami", ct: Suite.Ct);

        await harness.SignInAsync(browser, "app2", Suite.Ct);
        BrowserResponse app2Own = await browser.GetAsync("/api/reports/whoami", ct: Suite.Ct);
        BrowserResponse app2OnOrders = await browser.GetAsync($"/api/{app2Second}/whoami", ct: Suite.Ct);

        // Controls: each app's token is good at its own API, so the refusals are about the pairing.
        Assert.Equal(HttpStatusCode.OK, app1Own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, app2Own.StatusCode);

        // reports binds no audience, so app 1's token validates and is refused as the wrong client.
        Assert.Equal(HttpStatusCode.Forbidden, app1OnReports.StatusCode); // Not an Errors row: the mock API's own answer, passed back.
        Assert.Contains("\"error\":\"client_not_admitted\"", app1OnReports.Body, StringComparison.Ordinal);

        // orders binds its audience, and app 2 asks for no resource: its token fails validation there.
        // The mock API's invalid_token does not say why, so the provider's record of each login says
        // it: app 1 named a resource (its token's aud), app 2 named none (no aud for orders to accept).
        Assert.NotNull(harness.Idp.Authorizations.Last(authorization => authorization.ClientId == harness.App("app1").ClientId).Resource);
        Assert.Null(harness.Idp.Authorizations.Last(authorization => authorization.ClientId == harness.App("app2").ClientId).Resource);
        Assert.Equal(HttpStatusCode.Unauthorized, app2OnOrders.StatusCode); // Not an Errors row: the mock API's own answer, passed back.
        Assert.Contains("\"error\":\"invalid_token\"", app2OnOrders.Body, StringComparison.Ordinal);
        Assert.Contains("\"api\":\"orders\"", app2OnOrders.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task N19_an_unauthenticated_api_call_and_session_read_are_401_problems_and_never_redirects()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(browser, app);

        // Fetches, as the SPA makes them, with redirects left unfollowed so one would be seen. (A
        // top-level navigation to /api is refused by the request gate before authentication, which
        // is N-07's subject, not this row's.)
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", SignedIn.Manual, Suite.Ct);
        BrowserResponse session = await browser.GetAsync("/bff/session", SignedIn.Manual, Suite.Ct);

        Contract.ErrorRow expected = Contract.Error("unauthenticated");
        foreach (BrowserResponse response in (BrowserResponse[])[api, session])
        {
            Assert.Equal((HttpStatusCode)expected.Status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Header("Content-Type"));
            Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);
            Assert.Null(response.Header("Location"));
        }

        // No grant at the provider: without a session there was no token to fetch or refresh.
        Assert.Empty(harness.Idp.Grants);
    }

    private static string SecondApi(string appId) =>
        FixtureFiles.AppDefinition(appId)["apis"]!.AsArray()[1]!["name"]!.GetValue<string>();
}
