using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Negative;

/// <summary>N-01, N-19 and N-27 in a real browser (docs/plan/04-tests.md, tier D).</summary>
public sealed class SessionTests
{
    [Fact]
    public async Task N01_app1s_session_cookie_presented_to_app2_gets_401_on_the_api_and_a_fresh_challenge_on_navigation()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
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

        foreach (string path in new[] { "/api/reports/whoami", "/bff/session" })
        {
            FetchResult refused = await profile.FetchAsync(path);
            Assert.Equal(401, refused.Status);
            Assert.Equal(Contract.Error("No session").Code, refused.Code);
        }

        // A navigation is challenged afresh: to the provider, for app 2's own client. The load balancer
        // holds the callback, so the browser stops after the provider.
        List<Uri> navigations = Navigations(profile);
        stack.LoadBalancer.Hold(url => url.AbsolutePath == "/signin-oidc");
        await profile.Page.GotoAsync(app2.Url("/").ToString());
        Uri authorize = Assert.Single(navigations, url => url.GetLeftPart(UriPartial.Path) == stack.Harness.Idp.AuthorizeEndpoint);
        Assert.Contains("client_id=" + app2.ClientId, authorize.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(stack.LoadBalancer.Exchanges, exchange => exchange.Url == app2.Url("/") && exchange.Status == 200);
        stack.LoadBalancer.Release();

        // Control: the same cookie is a session on app 1.
        await profile.OnOriginOfAsync(app1);
        Assert.Equal(200, (await profile.FetchAsync("/bff/session")).Status);
    }

    [Fact]
    public async Task N19_an_unauthenticated_api_call_and_session_read_are_401_problems_and_never_redirects()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.OnOriginOfAsync(app);

        foreach (string path in new[] { "/api/orders/whoami", "/bff/session" })
        {
            FetchResult answer = await profile.FetchAsync(path);

            // redirect: "manual" would show a redirect as an opaque response with status 0.
            Assert.Equal(401, answer.Status);
            Assert.Equal("basic", answer.Type);
            Assert.Equal("application/problem+json", answer.ContentType);
            Assert.Equal(Contract.Error("No session").Code, answer.Code);
        }
    }

    [Fact]
    public async Task N27_get_and_post_to_signout_oidc_with_a_live_session_are_404_and_the_session_survives()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        IResponse? get = await profile.Page.GotoAsync(app.Url("/signout-oidc").ToString());
        Assert.Equal(404, get?.Status);

        // A form post, as a page would send the framework's remote sign-out.
        await profile.Page.SetContentAsync($"<form method=\"post\" action=\"{app.Url("/signout-oidc")}\"><button>go</button></form>");
        IResponse post = await profile.Page.RunAndWaitForResponseAsync(
            () => profile.Page.ClickAsync("button"), response => response.Url == app.Url("/signout-oidc").ToString());
        Assert.Equal(404, post.Status);
        Assert.Equal("POST", post.Request.Method);

        await profile.Page.GotoAsync(app.Url("/").ToString());
        await profile.WaitSignedInAsync(app);
    }

    private static List<Uri> Navigations(Profile profile)
    {
        List<Uri> navigations = [];
        profile.Page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest)
            {
                navigations.Add(new Uri(request.Url));
            }
        };
        return navigations;
    }
}
