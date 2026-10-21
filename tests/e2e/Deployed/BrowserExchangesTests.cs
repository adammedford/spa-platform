using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed stack's record of what the browser exchanged with the cell (<see cref="BrowserExchanges"/>),
/// held against the local stand-in load balancer's record of the same requests, for the exchanges the
/// negative rows read (#211): a call CORS refused, its preflight, and a framed shell.
/// </summary>
public sealed class BrowserExchangesTests
{
    [Fact]
    public async Task A_sibling_pages_refused_calls_are_kept_as_sent_and_its_frame_as_received()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        BrowserExchanges recorded = new(LocalStack.Domain);
        await recorded.AttachAsync(profile.Page);
        await profile.SignInAsync(app);

        await profile.OpenForeignPageAsync(((IBrowserStack)stack).Sibling, $"<!doctype html><title>framer</title><iframe src=\"{app.Url("/")}\"></iframe>");
        await profile.FetchAsync(app.Url("/bff/session").ToString(), credentials: "include");
        await profile.FetchAsync(app.Url("/api/orders/echo").ToString(), "POST", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "{}", "include");
        IReadOnlyList<Exchange> browser = await recorded.AllAsync();

        // The refused call: the gateway answered it, and the browser kept the answer from the page and
        // from Playwright, so the network stack's own record of it is what is kept, with no body.
        Exchange answered = Assert.Single(stack.LoadBalancer.Exchanges, exchange => exchange.Url == app.Url("/bff/session") && exchange.Sent("Sec-Fetch-Site") == "same-site");
        Assert.Equal(Contract.Error("origin_rejected").Status, answered.Status);
        Exchange refused = Assert.Single(browser, exchange => exchange.Url == app.Url("/bff/session") && exchange.Sent("Sec-Fetch-Site") == "same-site");
        Assert.Equal(answered.Status, refused.Status);
        Assert.Equal(answered.Header("Content-Security-Policy"), refused.Header("Content-Security-Policy"));
        Assert.Empty(refused.Body);
        Assert.Equal(answered.Sent("Cookie"), refused.Sent("Cookie"));
        Assert.Equal(answered.Sent("Origin"), refused.Sent("Origin"));

        // The preflight reached the load balancer and was refused, so the POST never left the browser and
        // is no exchange; the browser reported the preflight to neither Playwright nor the protocol.
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Method == "OPTIONS" && exchange.Status == Contract.Error("origin_rejected").Status);
        Assert.DoesNotContain(browser, exchange => exchange.Method == "OPTIONS");
        Assert.DoesNotContain(browser, exchange => exchange.Method == "POST");

        // The frame's response is reported as it arrived, before the browser refused to render it.
        Exchange framed = Assert.Single(browser, exchange => exchange.Url == app.Url("/") && exchange.Sent("Sec-Fetch-Dest") == "iframe");
        Assert.Equal(200, framed.Status);
        Assert.Contains("frame-ancestors 'none'", framed.Header("Content-Security-Policy") ?? "", StringComparison.Ordinal);
    }
}
