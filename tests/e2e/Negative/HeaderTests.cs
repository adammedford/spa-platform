using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-23 and N-24 in a real browser (docs/plan/04-tests.md, tier D), on the stack
/// <see cref="StackSwitch"/> chooses: what the browser received from <c>/bff</c>, and the framing and
/// embedding headers, read off the stack's record of what the browser received, with the browser's own
/// enforcement of <c>frame-ancestors</c> beside them.
/// </summary>
public sealed class HeaderTests
{
    [Fact]
    public async Task N23_no_bff_response_the_browser_received_holds_a_token_or_a_token_shaped_string()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        LocalStack? local = stack as LocalStack;

        // Login, the session read, an API call, and logout: every /bff endpoint. Locally the call
        // refreshes, so the rotated tokens are among those searched for; on the cell a token cannot be
        // issued expired, and waiting one out is F-03's (ExposureTests does the same).
        if (local is not null)
        {
            await local.SignInWithExpiredAccessTokenAsync(profile, app);
        }
        else
        {
            await profile.SignInAsync(app);
        }

        Assert.Equal(200, (await profile.FetchAsync("/api/orders/whoami")).Status);
        await stack.StopAtProviderLoginAsync(profile, () => profile.Page.ClickAsync("form[action='/bff/logout'] button"));

        Exchange[] bff = [.. (await stack.ExchangesAsync()).Where(exchange => exchange.Url.AbsolutePath.StartsWith("/bff/", StringComparison.Ordinal))];
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/login");
        Assert.Contains(bff, exchange => exchange.Url.AbsolutePath == "/bff/logout");
        Exchange session = Assert.Single(bff, exchange => exchange.Url.AbsolutePath == "/bff/session" && exchange.Status == 200);

        // The search can fail: the session body is in it, csrfToken and all. No message prints the bodies
        // or a token found in them: on the cell they are real (AGENTS.md).
        string bodies = string.Join("\n", bff.Select(exchange => exchange.BodyText));
        Assert.True(bodies.Contains("csrfToken", StringComparison.Ordinal), "the BFF bodies hold no csrfToken, so the search could not fail.");
        Assert.True(bodies.Contains(session.BodyText, StringComparison.Ordinal), "the BFF bodies do not hold the session body, so the search could not fail.");

        TokenShape.AssertNoJwtIn(bodies, "a BFF response body");
        if (local is not null)
        {
            // In H, none of the token values the provider issued either.
            TokenShape.AssertNoneIssuedIn(local.Harness.Idp, bodies, "a BFF response body");
        }
    }

    [Fact]
    public async Task N24_a_shell_an_asset_a_bff_response_and_an_api_error_all_carry_the_framing_and_embedding_headers()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);
        Assert.Equal(Contract.Error("unknown_api").Status, (await profile.FetchAsync("/api/unlisted/x")).Status);

        IReadOnlyList<Exchange> exchanges = await stack.ExchangesAsync();
        Exchange[] four =
        [
            exchanges.Last(exchange => exchange.Url == app.Url("/") && exchange.Status == 200),
            exchanges.Last(exchange => exchange.Url.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) && exchange.Status == 200),
            exchanges.Last(exchange => exchange.Url.AbsolutePath == "/bff/session"),
            exchanges.Last(exchange => exchange.Url.AbsolutePath == "/api/unlisted/x"),
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
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        // A sibling page frames the shell. The browser requests it (the frame's request reaches the
        // gateway) and then refuses to render it, so the frame holds no document from the app.
        await profile.OpenForeignPageAsync(stack.Sibling, $"<!doctype html><title>framer</title><iframe id=\"frame\" src=\"{app.Url("/")}\"></iframe>");
        Microsoft.Playwright.IFrame? frame = null;
        for (int attempt = 0; attempt < 100 && frame is null; attempt++)
        {
            frame = profile.Page.Frames.FirstOrDefault(candidate => candidate != profile.Page.MainFrame);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(frame);
        await frame.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.Load);
        Assert.Contains(await stack.ExchangesAsync(), exchange => exchange.Url == app.Url("/") && exchange.Sent("Sec-Fetch-Dest") == "iframe");
        Assert.Equal(0, await frame.Locator("#marker").CountAsync());
    }
}
