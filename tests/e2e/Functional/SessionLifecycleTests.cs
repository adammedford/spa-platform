using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-03 and F-04 in a real browser (docs/plan/04-tests.md, tier D): the fixture shell's own buttons
/// drive the API call and the logout, so what is exercised is what a user's click does.
/// </summary>
public sealed class SessionLifecycleTests
{
    /// <summary>The line the token source writes per refresh (src/Platform.Identity/AccessTokenSource.cs), which the D tier counts.</summary>
    private const string RefreshEvent = "Refreshed the access token for app ";

    [Fact]
    public async Task F03_a_call_after_token_expiry_succeeds_after_one_refresh_and_the_log_holds_one_refresh_event()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        await stack.SignInWithExpiredAccessTokenAsync(profile, app);
        Assert.Empty(await stack.LogLinesAsync(RefreshEvent));

        await profile.Page.ClickAsync("#call-api");

        // Past expiry is outside the mock API's clock skew, which is zero: only a refreshed token gets a 200.
        await Assertions.Expect(profile.Page.Locator("#api-result")).ToContainTextAsync("200 ");
        Assert.Contains(app.ClientId, await profile.Page.Locator("#api-result").TextContentAsync(), StringComparison.Ordinal);
        System.Text.Json.JsonElement refresh = Assert.Single(await stack.SettledLogLinesAsync(RefreshEvent));
        Assert.Equal(app.AppId, refresh.GetProperty("app_id").GetString());
        if (stack is LocalStack local)
        {
            Assert.Single(local.Harness.Idp.RefreshGrants); // In H the provider counts one grant.
        }
    }

    [Fact]
    public async Task F04_logout_ends_the_session_clears_the_antiforgery_cookie_and_the_next_navigation_is_the_providers_login()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);
        Assert.True(await profile.HoldsAsync(app, Contract.PlatformCookie("-csrf")));
        BrowserContextCookiesResult session = Assert.Single(await profile.CookiesOfAsync(app), cookie => cookie.Name == Contract.PlatformCookie("-session"));

        List<Uri> navigations = profile.RecordNavigations();

        // The shell's own logout form. Its post ends in a redirect to the provider's logout, and Chromium
        // applies form-action to that redirect (contracts/http-contract.md -> "The baseline content
        // security policy"): with the provider origin taken out of form-action, Chromium 153 stays on the
        // shell and this test fails before the provider's login. The provider origin in the policy is
        // what lets the logout through. The stack stops the browser at the provider's login: the test
        // provider would approve at once and sign straight back in.
        await stack.StopAtProviderLoginAsync(profile, () => profile.Page.ClickAsync("form[action='/bff/logout'] button"));
        if (stack is LocalStack local)
        {
            Assert.Equal(1, local.Harness.Idp.EndSessions);
        }

        // Both cookies are gone from the browser: the session cookie and the antiforgery cookie. What it
        // holds now is the login state of the login the provider's page would complete.
        Assert.False(await profile.HoldsAsync(app, Contract.PlatformCookie("-session")));
        Assert.False(await profile.HoldsAsync(app, Contract.PlatformCookie("-csrf")));

        // The navigation back to the app after the provider's logout went to the provider's login, for
        // this app's client, and did not reach the shell.
        // A failure prints the navigations only as Redacted does: they carry the provider's state.
        int endSession = navigations.FindIndex(url => url.GetLeftPart(UriPartial.Path) == stack.EndSessionEndpoint);
        Uri[] after = [.. navigations.Skip(endSession + 1)];
        Assert.True(endSession >= 0, Redacted.Urls(navigations));
        Uri[] authorizations = [.. after.Where(url => url.GetLeftPart(UriPartial.Path) == stack.AuthorizeEndpoint)];
        Assert.True(authorizations.Length == 1, $"{authorizations.Length} authorize requests after the logout in {Redacted.Urls(navigations)}");
        Assert.Equal(app.ClientId, QueryHelpers.ParseQuery(authorizations[0].Query)["client_id"].ToString());

        // The session is gone at the gateway, not only from the browser: the cookie it held before
        // logout, put back, is anonymous.
        await profile.OnOriginOfAsync(app);
        await profile.Context.AddCookiesAsync([Replant(session)]);
        FetchResult replay = await profile.FetchAsync("/bff/session");
        Assert.Equal(Contract.Error("unauthenticated").Status, replay.Status);
    }

    /// <summary>A cookie as the browser held it, to put back in the jar under the same name, host and attributes.</summary>
    private static Cookie Replant(BrowserContextCookiesResult cookie) => new()
    {
        Name = cookie.Name,
        Value = cookie.Value,
        Domain = cookie.Domain,
        Path = cookie.Path,
        Secure = cookie.Secure,
        HttpOnly = cookie.HttpOnly,
        SameSite = cookie.SameSite,
    };
}
