using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-01 and F-02 in a real browser (docs/plan/04-tests.md, tier D): two apps on two hosts of one
/// gateway, each signing in with its own client and returning to its own callback.
/// </summary>
public sealed class SignInTests
{
    [Fact]
    public async Task F01_signed_in_to_both_apps_each_host_serves_its_own_shell_from_the_one_gateway()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");

        await profile.SignInAsync(app1);
        await profile.SignInAsync(app2);

        // Each shell is the app's own fixture SPA, which names its app (fixtures/spa/{appId}/index.html),
        // and its script reached that app's session: both are signed in at once in one browser.
        foreach (HarnessApp app in new[] { app1, app2 })
        {
            await profile.Page.GotoAsync(app.Url("/").ToString());
            await profile.WaitSignedInAsync(app);
            Assert.Equal(app.AppId, await profile.Page.Locator("#app").GetAttributeAsync("data-app"));
        }

        // One load balancer and one gateway answered both hosts.
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url == app1.Url("/") && exchange.Status == 200);
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url == app2.Url("/") && exchange.Status == 200);
    }

    [Fact]
    public async Task F02_each_login_goes_to_the_provider_with_its_own_client_and_returns_to_its_own_callback()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        List<Uri> navigations = [];
        profile.Page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest)
            {
                navigations.Add(new Uri(request.Url));
            }
        };

        foreach (string appId in new[] { "app1", "app2" })
        {
            HarnessApp app = stack.App(appId);
            navigations.Clear();
            await profile.SignInAsync(app);

            Uri authorize = Assert.Single(navigations, url => url.GetLeftPart(UriPartial.Path) == stack.Harness.Idp.AuthorizeEndpoint);
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(authorize.Query);
            Assert.Equal(app.ClientId, query["client_id"].ToString());
            Assert.Equal(app.Url("/signin-oidc").ToString(), query["redirect_uri"].ToString());

            // The browser came back to this app's own callback, after the provider and before the shell.
            int callback = navigations.FindIndex(url => url.GetLeftPart(UriPartial.Path) == app.Url("/signin-oidc").ToString());
            Assert.True(callback > navigations.IndexOf(authorize), string.Join(" -> ", navigations));
            Assert.Equal(app.Url("/"), navigations[^1]);
        }
    }
}
