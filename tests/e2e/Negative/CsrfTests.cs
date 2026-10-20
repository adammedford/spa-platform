using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-06 in a real browser (docs/plan/04-tests.md, tier D): an unsafe API call from the app's own page
/// with no CSRF token, another user's token, another app's token and cookie pair, and a token from
/// before the last logout. 403 <c>csrf_failed</c> each time, and the call with the page's own token
/// succeeds, so the refusals are about the token.
/// </summary>
public sealed class CsrfTests
{
    private static readonly string Header = Contract.CsrfHeaderName;

    [Fact]
    public async Task N06_an_unsafe_call_without_the_sessions_own_token_is_403_csrf_failed_each_way()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile alice = await stack.OpenAsync();
        await using Profile mallory = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");
        string csrfCookie = Contract.PlatformCookie("-csrf");

        // Another user's token: mallory's own session on app 1.
        stack.Harness.Idp.DefaultSubject = "mallory";
        await mallory.SignInAsync(app1);
        string mallorysToken = await TokenAsync(mallory);

        // A token from before the last logout: alice signs in, keeps her token, logs out through the
        // shell, and signs in again.
        stack.Harness.Idp.DefaultSubject = "alice";
        await alice.SignInAsync(app1);
        string beforeLogout = await TokenAsync(alice);
        stack.LoadBalancer.Hold(url => url.AbsolutePath == "/signin-oidc");
        await alice.Page.ClickAsync("form[action='/bff/logout'] button");
        await Assertions.Expect(alice.Page).ToHaveTitleAsync(TlsLoadBalancer.HeldTitle);
        stack.LoadBalancer.Release();
        await alice.SignInAsync(app1);

        // Another app's token and cookie pair: alice's own on app 2.
        await alice.SignInAsync(app2);
        string app2Token = await TokenAsync(alice);
        BrowserContextCookiesResult app2Cookie = Assert.Single(await alice.CookiesOfAsync(app2), cookie => cookie.Name == csrfCookie);
        BrowserContextCookiesResult app1Cookie = Assert.Single(await alice.CookiesOfAsync(app1), cookie => cookie.Name == csrfCookie);

        await alice.Page.GotoAsync(app1.Url("/").ToString());
        await alice.WaitSignedInAsync(app1);
        string own = await TokenAsync(alice);

        await RefusedAsync(stack, alice, null);
        await RefusedAsync(stack, alice, mallorysToken);
        await RefusedAsync(stack, alice, beforeLogout);

        // App 2's antiforgery cookie in app 1's place, with app 2's token.
        await alice.Context.AddCookiesAsync([Csrf(app1, app2Cookie.Value)]);
        await RefusedAsync(stack, alice, app2Token);

        // Control: app 1's own pair is accepted.
        await alice.Context.AddCookiesAsync([Csrf(app1, app1Cookie.Value)]);
        FetchResult accepted = await alice.FetchAsync("/api/orders/echo", "POST", Headers(own), "{}");
        Assert.Equal(200, accepted.Status);
    }

    private static async Task RefusedAsync(LocalStack stack, Profile profile, string? token)
    {
        int mark = stack.RequestLines.Count;
        FetchResult refused = await profile.FetchAsync("/api/orders/echo", "POST", token is null ? null : Headers(token), "{}");
        Assert.Equal(403, refused.Status);
        Assert.Equal(Contract.Error("csrf_failed").Code, refused.Code);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.CsrfToken);
    }

    private static Dictionary<string, string> Headers(string token) => new() { [Header] = token, ["Content-Type"] = "application/json" };

    /// <summary>The <c>csrfToken</c> the page's own <c>/bff/session</c> hands out (contracts/http-contract.md).</summary>
    private static async Task<string> TokenAsync(Profile profile)
    {
        FetchResult session = await profile.FetchAsync("/bff/session");
        Assert.Equal(200, session.Status);
        using JsonDocument body = JsonDocument.Parse(session.Body);
        return body.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private static Cookie Csrf(HarnessApp app, string value) => new()
    {
        Name = Contract.PlatformCookie("-csrf"), Value = value, Url = app.Origin.ToString(), Secure = true, HttpOnly = true, SameSite = SameSiteAttribute.Strict,
    };
}
