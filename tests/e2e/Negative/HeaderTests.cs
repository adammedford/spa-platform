using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-23 and N-24 in a real browser (docs/plan/04-tests.md, tier D): what the browser received from
/// <c>/bff</c>, and the framing and embedding headers, read off the load balancer's record of what it
/// sent the browser, with the browser's own enforcement of <c>frame-ancestors</c> beside them.
/// </summary>
public sealed class HeaderTests
{
    [Fact]
    public async Task N23_no_bff_response_the_browser_received_holds_a_token_or_a_token_shaped_string()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        // Login, the session read, an API call that refreshes, and logout: every /bff endpoint.
        stack.Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await profile.SignInAsync(app);
        stack.Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        Assert.Equal(200, (await profile.FetchAsync("/api/orders/whoami")).Status);
        stack.LoadBalancer.Hold(url => url.AbsolutePath == "/signin-oidc");
        await profile.Page.ClickAsync("form[action='/bff/logout'] button");
        await Microsoft.Playwright.Assertions.Expect(profile.Page).ToHaveTitleAsync(TlsLoadBalancer.HeldTitle);

        Exchange[] bff = [.. stack.LoadBalancer.Exchanges.Where(exchange => exchange.Url.AbsolutePath.StartsWith("/bff/", StringComparison.Ordinal))];
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/login");
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/logout");
        Exchange session = Assert.Single(bff, exchange => exchange.Url.AbsolutePath == "/bff/session" && exchange.Status == 200);

        // The search can fail: the session body is in it, csrfToken and all.
        string bodies = string.Join("\n", bff.Select(exchange => exchange.BodyText));
        Assert.Contains("csrfToken", bodies, StringComparison.Ordinal);
        Assert.Contains(session.BodyText, bodies, StringComparison.Ordinal);

        Assert.DoesNotMatch(TokenShape.Jwt(), bodies);
        Assert.NotEmpty(TokenShape.IssuedBy(stack.Harness.Idp));
        Assert.All(TokenShape.IssuedBy(stack.Harness.Idp), token => Assert.DoesNotContain(token, bodies, StringComparison.Ordinal));
    }

    [Fact]
    public async Task N24_a_shell_an_asset_a_bff_response_and_an_api_error_all_carry_the_framing_and_embedding_headers()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);
        Assert.Equal(404, (await profile.FetchAsync("/api/unlisted/x")).Status);

        Exchange[] four =
        [
            stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url == app.Url("/") && exchange.Status == 200),
            stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) && exchange.Status == 200),
            stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url.AbsolutePath == "/bff/session"),
            stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url.AbsolutePath == "/api/unlisted/x"),
        ];

        foreach (Exchange exchange in four)
        {
            Assert.Contains("frame-ancestors 'none'", exchange.Header("Content-Security-Policy") ?? "", StringComparison.Ordinal);
            Assert.Equal(Contract.FixedResponseHeaders["X-Content-Type-Options"], exchange.Header("X-Content-Type-Options"));
            Assert.Equal(Contract.FixedResponseHeaders["Cross-Origin-Resource-Policy"], exchange.Header("Cross-Origin-Resource-Policy"));
        }
    }

    [Fact]
    public async Task N24_the_browser_refuses_to_frame_the_shell_in_another_page()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        // A sibling page frames the shell. The browser requests it (the frame's request reaches the
        // gateway) and then refuses to render it, so the frame holds no document from the app.
        Uri sibling = new($"https://evil.{LocalStack.Domain}:{app.Origin.Port}/");
        await profile.OpenForeignPageAsync(sibling, $"<!doctype html><title>framer</title><iframe id=\"frame\" src=\"{app.Url("/")}\"></iframe>");
        Microsoft.Playwright.IFrame? frame = null;
        for (int attempt = 0; attempt < 100 && frame is null; attempt++)
        {
            frame = profile.Page.Frames.FirstOrDefault(candidate => candidate != profile.Page.MainFrame);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(frame);
        await frame.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.Load);
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url == app.Url("/") && exchange.Sent("Sec-Fetch-Dest") == "iframe");
        Assert.Equal(0, await frame.Locator("#marker").CountAsync());
    }
}
