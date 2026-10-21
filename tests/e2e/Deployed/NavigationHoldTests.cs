using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed stack's way of stopping a login at its callback (#211), proven on the local stack's
/// browser, where the stand-in load balancer can say what reached it: the cell has no stand-in to hold
/// a request, so the browser itself must.
/// </summary>
public sealed class NavigationHoldTests
{
    [Fact]
    public async Task A_held_callback_never_leaves_the_browser_and_the_login_it_stopped_can_still_finish()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();

        // The callback is the third hop of a redirect chain that starts at /bff/login and passes the
        // provider, which a Playwright route would not see.
        Uri held = await NavigationHold.HoldAsync(profile.Page, callback, () => profile.Page.GotoAsync(app.Url("/bff/login").ToString()));

        // Asserted without printing the code or the state (Redacted).
        Assert.Equal(callback, Redacted.Url(held));
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(held.Query);
        Assert.True(query.ContainsKey("code") && query.ContainsKey("state"), "the held callback carries no code or no state");
        Assert.Equal(NavigationHold.HeldTitle, await profile.Page.TitleAsync());
        Assert.DoesNotContain(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc");
        Assert.Equal(2, (await profile.CookiesOfAsync(app)).Count);

        // Released, the same callback is a login: the code was not spent, and the browser's later
        // requests are its own again, Fetch Metadata and all.
        await profile.Page.GotoAsync(held.ToString());
        await profile.WaitSignedInAsync(app);
    }

    [Fact]
    public async Task A_hold_that_nothing_reaches_fails_naming_the_url_it_waited_for()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        TimeoutException missed = await Assert.ThrowsAsync<TimeoutException>(() =>
            NavigationHold.HoldAsync(profile.Page, app.Url("/signin-oidc").ToString(), () => profile.OnOriginOfAsync(app), TimeSpan.FromSeconds(2)));
        Assert.Contains("/signin-oidc", missed.Message, StringComparison.Ordinal);

        // And the page is not left held: the next callback-shaped request reaches the gateway.
        IResponse? passed = await profile.Page.GotoAsync(app.Url("/signin-oidc").ToString());
        Assert.NotNull(passed);
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc");
    }
}
