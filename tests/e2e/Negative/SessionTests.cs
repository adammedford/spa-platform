using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-01, N-19 and N-27 in a real browser (docs/plan/04-tests.md, tier D), on the stack
/// <see cref="StackSwitch"/> chooses: the local one by default, the cell in a sitting (#211).
/// </summary>
public sealed class SessionTests
{
    [Fact]
    public async Task N01_app1s_session_cookie_presented_to_app2_gets_401_on_the_api_and_a_fresh_challenge_on_navigation()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");
        string sessionCookie = Contract.PlatformCookie("-session");

        await profile.SignInAsync(app1);
        BrowserContextCookiesResult session = Assert.Single(await profile.CookiesOfAsync(app1), cookie => cookie.Name == sessionCookie);

        // App 1's cookie on app 2's host, under its own name: what the browser would send if the prefix
        // did not keep it on app 1's host.
        await profile.Context.AddCookiesAsync([new Cookie
        {
            Name = session.Name, Value = session.Value, Url = app2.Origin.ToString(), Secure = true, HttpOnly = true, SameSite = SameSiteAttribute.Lax,
        }]);
        await profile.OnOriginOfAsync(app2);

        // A cookie that resolves to no session for this app is auth.session (contracts/request-gate.md),
        // one line per refusal.
        LogMark mark = stack.MarkLog();
        foreach (string path in new[] { "/api/reports/whoami", "/bff/session" })
        {
            FetchResult refused = await profile.FetchAsync(path);
            Assert.Equal(Contract.Error("unauthenticated").Status, refused.Status);
            Assert.Equal(Contract.Error("unauthenticated").Code, refused.Code);
            await stack.AssertRuleLoggedAsync(mark, RuleIds.AuthSession, app2.AppId);
        }

        // A navigation is challenged afresh: to the provider, for app 2's own client. The login is held at
        // its callback, so the browser never reaches app 2's shell. On the cell the provider knows this
        // browser from app 1's login and sends it straight back to that callback.
        List<Uri> navigations = profile.RecordNavigations();
        await profile.StopAtCallbackAsync(app2, () => profile.Page.GotoAsync(app2.Url("/").ToString()));
        Uri[] authorizations = [.. navigations.Where(url => url.GetLeftPart(UriPartial.Path) == stack.AuthorizeEndpoint)];
        Assert.True(authorizations.Length == 1, $"{authorizations.Length} authorize requests in {Redacted.Urls(navigations)}");
        Assert.Equal(app2.ClientId, QueryHelpers.ParseQuery(authorizations[0].Query)["client_id"].ToString());
        Assert.DoesNotContain(await stack.ExchangesAsync(), exchange => exchange.Url == app2.Url("/") && exchange.Status == 200);

        // Control: the same cookie is a session on app 1.
        await profile.OnOriginOfAsync(app1);
        Assert.Equal(200, (await profile.FetchAsync("/bff/session")).Status);
    }

    [Fact]
    public async Task N19_an_unauthenticated_api_call_and_session_read_are_401_problems_and_never_redirects()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.OnOriginOfAsync(app);

        foreach (string path in new[] { "/api/orders/whoami", "/bff/session" })
        {
            FetchResult answer = await profile.FetchAsync(path);

            // redirect: "manual" would show a redirect as an opaque response with status 0.
            Assert.Equal(Contract.Error("unauthenticated").Status, answer.Status);
            Assert.Equal("basic", answer.Type);
            Assert.Equal("application/problem+json", answer.ContentType);
            Assert.Equal(Contract.Error("unauthenticated").Code, answer.Code);
        }
    }

    [Fact]
    public async Task N27_get_and_post_to_signout_oidc_with_a_live_session_are_404_and_the_session_survives()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        // A reserved path that is switched off: contracts/http-contract.md -> Errors has it in the
        // not_found row's When text, and contracts/request-gate.md -> Rules by path says /signout-oidc
        // is always 404.
        Contract.ErrorRow reserved = Contract.Error("not_found");
        LogMark mark = stack.MarkLog();
        IResponse? get = await profile.Page.GotoAsync(app.Url("/signout-oidc").ToString());
        Assert.Equal(reserved.Status, get?.Status);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.PathReserved, app.AppId);

        // A form post, as a page would send the framework's remote sign-out.
        await profile.Page.SetContentAsync($"<form method=\"post\" action=\"{app.Url("/signout-oidc")}\"><button>go</button></form>");
        IResponse post = await profile.Page.RunAndWaitForResponseAsync(
            () => profile.Page.ClickAsync("button"), response => response.Url == app.Url("/signout-oidc").ToString());
        Assert.Equal(reserved.Status, post.Status);
        Assert.Equal("POST", post.Request.Method);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.PathReserved, app.AppId);

        await profile.Page.GotoAsync(app.Url("/").ToString());
        await profile.WaitSignedInAsync(app);
    }
}
