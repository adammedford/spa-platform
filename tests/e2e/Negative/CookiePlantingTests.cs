using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-25 and N-26 (docs/plan/04-tests.md): their deciding run is in a real browser, because they rest
/// on the browser enforcing the <c>__Host-</c> prefix, which the hermetic test browser does not do
/// (R-D3). The sibling is app 2's own shell, its script under the attacker's control, as an XSS in a
/// sibling SPA would put it: it plants cookies for the parent domain with <c>document.cookie</c>.
/// </summary>
public sealed class CookiePlantingTests
{
    /// <summary>The value a sibling plants, so a test can tell its cookie from the gateway's.</summary>
    private const string Planted = "planted-by-a-sibling";

    /// <summary>A cookie with no prefix, planted the same way, so a refusal is about the prefix and not the planting.</summary>
    private const string Control = "sibling-control";

    [Fact]
    public async Task N25_a_sibling_cannot_set_a_platform_cookie_for_the_parent_domain_in_any_letter_case()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        await profile.SignInAsync(stack.App("app1"));
        await profile.SignInAsync(stack.App("app2"));

        // Every platform cookie, with a suffix in place of a wildcard as the framework adds one per login,
        // under its exact name and two other letter cases.
        string[] names = [.. Contract.PlatformCookieNames
            .Select(name => name.Replace("*", "planted", StringComparison.Ordinal))
            .SelectMany(name => new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant() })
            .Distinct(StringComparer.Ordinal)];
        foreach (string name in names.Append(Control))
        {
            await PlantAsync(profile, name);
        }

        // The control was set for the parent domain, so planting works; every platform name was refused.
        IReadOnlyList<BrowserContextCookiesResult> cookies = await profile.Context.CookiesAsync();
        Assert.Contains(cookies, cookie => cookie.Name == Control && cookie.Domain == "." + LocalStack.Domain);
        Assert.DoesNotContain(cookies, cookie => cookie.Value == Planted && cookie.Name != Control);

        // Chromium refuses the other letter cases as well, so no browser request can carry one. The
        // gateway's own rule for a request that does is shown with the raw client.
        HarnessApp app = stack.App("app1");
        string prefix = Contract.PlatformCookiePrefix;
        foreach (string variant in new[] { prefix.ToLowerInvariant(), prefix.ToUpperInvariant() })
        {
            int mark = stack.RequestLines.Count;
            using HttpRequestMessage request = new(HttpMethod.Get, app.Url("/bff/session"));
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Cookie", variant + "session=" + Planted);
            using HttpResponseMessage answer = await stack.SendRawAsync(request);

            Assert.Equal(Contract.Error("invalid_request").Status, (int)answer.StatusCode);
            Assert.Contains($"\"code\":\"{Contract.Error("invalid_request").Code}\"",
                await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            await stack.AssertRuleLoggedAsync(mark, RuleIds.GateCookieName);
        }
    }

    [Fact]
    public async Task N26_login_csrf_a_siblings_login_state_cookies_are_never_set_so_the_planted_callback_is_rejected()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile attacker = await stack.OpenAsync();
        await using Profile victim = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");

        // The attacker starts their own login to app 1 and stops at the callback: their code, their
        // state, and the login-state cookies that state is bound to.
        stack.Harness.Idp.DefaultSubject = "mallory";
        Uri callback = await attacker.StopAtCallbackAsync(app1);
        IReadOnlyList<BrowserContextCookiesResult> loginState = await attacker.CookiesOfAsync(app1);
        Assert.Equal(2, loginState.Count);

        // In the victim's browser, the attacker's script on a sibling SPA plants those cookies for the
        // parent domain, then sends the victim to app 1's callback with the attacker's code and state.
        stack.Harness.Idp.DefaultSubject = "alice";
        await victim.SignInAsync(stack.App("app2"));
        foreach (BrowserContextCookiesResult cookie in loginState)
        {
            await PlantAsync(victim, cookie.Name, cookie.Value);
        }

        await PlantAsync(victim, Control);
        Assert.Contains(await victim.Context.CookiesAsync(), cookie => cookie.Name == Control);

        // The cookies are never set: not for the parent domain, and not for app 1's host.
        Assert.DoesNotContain(await victim.Context.CookiesAsync(), cookie => loginState.Any(planted => planted.Name == cookie.Name));

        int mark = stack.RequestLines.Count;
        IResponse? rejected = await victim.Page.RunAndWaitForResponseAsync(
            () => victim.Page.EvaluateAsync("(url) => { window.location.assign(url); }", callback.ToString()),
            response => response.Url == callback.ToString());

        // So the callback is rejected, and no session is created: not the attacker's, not anyone's.
        Assert.Equal(400, rejected.Status); // Not an Errors row: the callback's failure page (Errors prose), not a problem body.
        await stack.AssertRuleLoggedAsync(mark, RuleIds.AuthCallback, app1.AppId);
        Assert.DoesNotContain(stack.Harness.Idp.Grants, grant => grant.GrantType == "authorization_code" && grant.ClientId == app1.ClientId);
        Assert.False(await victim.HoldsAsync(app1, Contract.PlatformCookie("-session")));

        // Control: the attacker's own callback, in the attacker's browser with the cookies it was bound
        // to, is a login. The victim's refusal was the missing cookies, not a broken code.
        await attacker.Page.GotoAsync(callback.ToString());
        await attacker.WaitSignedInAsync(app1);
    }

    /// <summary>The sibling's script sets a cookie for the parent domain, with every attribute the <c>__Host-</c> prefix would otherwise accept.</summary>
    private static Task PlantAsync(Profile sibling, string name, string value = Planted) =>
        sibling.Page.EvaluateAsync(
            "([name, value, domain]) => { document.cookie = `${name}=${value}; Domain=${domain}; Path=/; Secure; SameSite=Lax`; }",
            new object[] { name, value, LocalStack.Domain });
}
