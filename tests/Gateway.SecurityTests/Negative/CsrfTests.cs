using System.Net;
using System.Text;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-06: "Unsafe API call with no CSRF token, with another user's token, with another app's token and
/// cookie pair, and with a token from before the last logout -> 403 <c>csrf_failed</c> each time."
/// (docs/plan/04-tests.md; ADR-0007; evidence R-E3, R-E4.)
///
/// <para>Every call is a signed-in <c>POST</c> from the app's own page, so the request gate passes it
/// and <c>UsePlatformAntiforgery</c> is what decides. Each refusal is read three ways: the status and
/// the <c>code</c> from the contract's Errors table, the rule id <see cref="RuleIds.CsrfToken"/> on the
/// request line, and nothing reaching the echo upstream. The control is the same call with the
/// browser's own current token, which reaches the upstream.</para>
/// </summary>
public sealed class CsrfTests
{
    private const string ApiPath = "/api/echo/orders";

    [Fact]
    public async Task N06_an_unsafe_call_with_no_token_is_403_csrf_failed()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        await SignedIn.CsrfTokenAsync(browser);

        await AssertRefusedAsync(harness, browser, headers: []);
        await AssertAcceptedAsync(harness, browser);
    }

    [Fact]
    public async Task N06_an_unsafe_call_with_another_users_token_is_403_csrf_failed()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();

        using TestBrowser other = harness.CreateBrowser();
        harness.Idp.DefaultSubject = "bob";
        await harness.SignInAsync(other, "app1", Suite.Ct);
        string bobs = await SignedIn.CsrfTokenAsync(other);

        using TestBrowser browser = harness.CreateBrowser();
        harness.Idp.DefaultSubject = "alice";
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string own = await SignedIn.CsrfTokenAsync(browser);
        Assert.NotEqual(own, bobs);

        await AssertRefusedAsync(harness, browser, [(Contract.CsrfHeaderName, bobs)]);
        await AssertAcceptedAsync(harness, browser);
    }

    [Fact]
    public async Task N06_an_unsafe_call_with_another_apps_token_and_cookie_pair_is_403_csrf_failed()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        HarnessApp app1 = harness.App("app1"), app2 = harness.App("app2");

        // The same user, signed in to both apps in one browser: a pair that is valid on app 2 for the
        // very identity that is calling app 1. Only the app binding can refuse it (R-E3).
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app2", Suite.Ct);
        string app2Token = await SignedIn.CsrfTokenAsync(browser);
        string app2CsrfCookie = Assert.Single(browser.Cookies.GetCookies(app2.Origin), cookie => cookie.Name == SignedIn.CsrfCookieName).Value;

        await harness.SignInAsync(browser, "app1", Suite.Ct);
        await SignedIn.CsrfTokenAsync(browser);
        string cookies = SignedIn.CsrfCookieName + "=" + app2CsrfCookie + "; "
            + SignedIn.SessionCookieName + "=" + SignedIn.SessionCookie(browser, app1);

        await AssertRefusedAsync(harness, browser, [(Contract.CsrfHeaderName, app2Token), ("Cookie", cookies)]);
        await AssertAcceptedAsync(harness, browser);
    }

    [Fact]
    public async Task N06_an_unsafe_call_with_a_token_from_before_the_last_logout_is_403_csrf_failed()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string before = await SignedIn.CsrfTokenAsync(browser);

        // Log out (the provider's logout is not followed: the gateway's half is what clears the
        // antiforgery cookie), then sign in again as the same user.
        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: SignedIn.Manual, ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string after = await SignedIn.CsrfTokenAsync(browser);
        Assert.NotEqual(before, after);

        await AssertRefusedAsync(harness, browser, [(Contract.CsrfHeaderName, before)]);
        await AssertAcceptedAsync(harness, browser);
    }

    /// <summary>A <c>POST</c> to the echo API from the app's page, with these headers over the browser's own.</summary>
    private static Task<BrowserResponse> PostAsync(TestBrowser browser, IEnumerable<(string Name, string? Value)> headers) =>
        browser.FetchAsync(
            HttpMethod.Post,
            ApiPath,
            new StringContent("{\"order\":1}", Encoding.UTF8, "application/json"),
            RequestOptions.With([.. headers]),
            Suite.Ct);

    private static async Task AssertRefusedAsync(GatewayHarness harness, TestBrowser browser, IEnumerable<(string Name, string? Value)> headers)
    {
        int reached = harness.Echo.Requests.Count;
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);

        BrowserResponse response = await PostAsync(browser, headers);

        Contract.ErrorRow expected = Contract.Error("antiforgery token was missing or wrong");
        Assert.Equal((HttpStatusCode)expected.Status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);
        Assert.Equal(RuleIds.CsrfToken, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        Assert.Equal(reached, harness.Echo.Requests.Count);
    }

    /// <summary>The control: the browser's own current token is accepted and the call reaches the upstream.</summary>
    private static async Task AssertAcceptedAsync(GatewayHarness harness, TestBrowser browser)
    {
        string token = await SignedIn.CsrfTokenAsync(browser);
        int reached = harness.Echo.Requests.Count;

        BrowserResponse response = await PostAsync(browser, [(Contract.CsrfHeaderName, token)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(reached + 1, harness.Echo.Requests.Count);
    }
}
