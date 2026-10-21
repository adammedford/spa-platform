using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-03, N-04 and N-05 in a real browser (docs/plan/04-tests.md, tier D). A login is stopped at its
/// callback by the load balancer (<see cref="Profile.StopAtCallbackAsync"/>), so the browser holds the
/// code, the state and the login-state cookies, and the test then sends the callback the row
/// describes. "Rejected" is read as the hermetic suite reads it: the callback's own answer, no session
/// cookie in the browser, <c>/bff/session</c> answering 401, and the rule in the log.
/// </summary>
public sealed partial class LoginTests
{
    [Fact]
    public async Task N03_app1s_callback_replayed_on_app2_with_its_login_cookies_is_rejected_and_creates_no_session()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");

        Uri callback = await profile.StopAtCallbackAsync(app1);
        IReadOnlyList<BrowserContextCookiesResult> loginCookies = await profile.CookiesOfAsync(app1);
        Assert.Contains(loginCookies, cookie => cookie.Name.StartsWith(Prefix("corr.*"), StringComparison.Ordinal));
        Assert.Contains(loginCookies, cookie => cookie.Name.StartsWith(Prefix("nonce.*"), StringComparison.Ordinal));

        // The same cookies on app 2's host, and app 1's callback, code and state, sent to app 2.
        await profile.Context.AddCookiesAsync([.. loginCookies.Select(cookie => On(app2, cookie))]);
        int mark = stack.RequestLines.Count;
        IResponse? rejected = await profile.Page.GotoAsync(app2.Url("/signin-oidc" + callback.Query).ToString());

        await AssertRejectedAsync(stack, profile, app2, rejected, mark);
        Assert.DoesNotContain(stack.Harness.Idp.Grants, grant => grant.GrantType == "authorization_code");

        // Control: the same callback on its own app is a login.
        await profile.Page.GotoAsync(callback.ToString());
        await profile.WaitSignedInAsync(app1);
    }

    [Fact]
    public async Task N04_a_code_issued_to_client1_injected_into_app2s_own_login_is_refused_by_the_provider()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile attacker = await stack.OpenAsync();
        await using Profile victim = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");

        // The attacker's code, issued to app 1's client and never redeemed.
        string code = Query(await attacker.StopAtCallbackAsync(app1), "code");

        // The victim's own login to app 2, with its own state and cookies, carrying the attacker's code.
        Uri own = await victim.StopAtCallbackAsync(app2);
        int mark = stack.RequestLines.Count;
        IResponse? rejected = await victim.Page.GotoAsync(app2.Url($"/signin-oidc?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(Query(own, "state"))}").ToString());

        await AssertRejectedAsync(stack, victim, app2, rejected, mark);
        TestIdp.TokenGrant exchange = Assert.Single(stack.Harness.Idp.Grants, grant => grant.GrantType == "authorization_code");
        Assert.Equal(app2.ClientId, exchange.ClientId);
        Assert.Equal("invalid_grant", exchange.Outcome);
    }

    [Theory]
    [InlineData("no correlation cookie")]
    [InlineData("no nonce cookie")]
    [InlineData("tampered state")]
    public async Task N05_a_callback_without_its_correlation_or_nonce_cookie_or_with_tampered_state_is_rejected(string tampering)
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        Uri callback = await profile.StopAtCallbackAsync(app);
        string state = Query(callback, "state");
        switch (tampering)
        {
            case "no correlation cookie":
                await profile.Context.ClearCookiesAsync(new() { NameRegex = new Regex("^" + Regex.Escape(Prefix("corr.*"))) });
                break;
            case "no nonce cookie":
                await profile.Context.ClearCookiesAsync(new() { NameRegex = new Regex("^" + Regex.Escape(Prefix("nonce.*"))) });
                break;
            default:
                // One character of the protected state changed.
                state = state[..^4] + (state[^4] == 'A' ? 'B' : 'A') + state[^3..];
                break;
        }

        int mark = stack.RequestLines.Count;
        IResponse? rejected = await profile.Page.GotoAsync(
            app.Url($"/signin-oidc?code={Uri.EscapeDataString(Query(callback, "code"))}&state={Uri.EscapeDataString(state)}").ToString());

        await AssertRejectedAsync(stack, profile, app, rejected, mark);

        // Control: a whole login on the same stack is a session.
        await profile.SignInAsync(app);
    }

    /// <summary>"Rejected. No session created": the callback's refusal, no session cookie, a 401 for the session, and the rule.</summary>
    private static async Task AssertRejectedAsync(LocalStack stack, Profile profile, HarnessApp app, IResponse? callback, int mark)
    {
        Assert.NotNull(callback);
        Assert.Equal(400, callback.Status); // Not an Errors row: the callback's failure page (Errors prose), not a problem body.
        Assert.False(await profile.HoldsAsync(app, Contract.PlatformCookie("-session")));
        await stack.AssertRuleLoggedAsync(mark, RuleIds.AuthCallback, app.AppId);

        await profile.OnOriginOfAsync(app);
        FetchResult session = await profile.FetchAsync("/bff/session");
        Assert.Equal(Contract.Error("unauthenticated").Status, session.Status);
    }

    /// <summary>A cookie the browser held, copied to another app's host under the same name and attributes.</summary>
    private static Cookie On(HarnessApp app, BrowserContextCookiesResult cookie) => new()
    {
        Name = cookie.Name,
        Value = cookie.Value,
        Url = app.Origin.ToString(),
        Secure = cookie.Secure,
        HttpOnly = cookie.HttpOnly,
        SameSite = cookie.SameSite,
    };

    /// <summary>A wildcard cookie name of the contract's Cookies table, up to its star.</summary>
    private static string Prefix(string suffix) => Contract.PlatformCookie(suffix).TrimEnd('*');

    private static string Query(Uri url, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)[name].ToString();
}
