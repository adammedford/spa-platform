using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed stack's foreign pages (#211), proven against the local stand-in load balancer: a
/// browser that, like the cell's, ignores no certificate error, trusts the foreign listener's key and
/// the stand-in's by name alone, and sends the sibling to the foreign listener ahead of the rule that
/// sends every other app-domain host on.
/// </summary>
public sealed class ForeignSitesTests
{
    [Theory]
    [InlineData("sibling", "same-site")]
    [InlineData("elsewhere", "cross-site")]
    public async Task A_foreign_page_loads_with_strict_certificate_checks_and_its_requests_reach_the_app_as_the_browsers_own(string initiator, string fetchSite)
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using ForeignSites foreign = await ForeignSites.StartAsync(LocalStack.Domain, TestContext.Current.CancellationToken);
        HarnessApp app = stack.App("app1");
        await using IBrowser browser = await Chromium.LaunchAsync(
            $"--host-resolver-rules={foreign.ResolverRules}, MAP *.{LocalStack.Domain} {stack.LoadBalancer.Endpoint}",
            ForeignSites.TrustSwitch(foreign.Spki, stack.LoadBalancer.Spki));
        await using IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = false });
        IPage page = await context.NewPageAsync();

        Uri url = initiator == "sibling" ? foreign.Sibling(app.Origin.Port) : ForeignSites.Elsewhere;
        foreign.Serve(url, "<!doctype html><title>foreign</title>");
        IResponse? loaded = await page.GotoAsync(url.ToString());
        Assert.Equal(200, loaded?.Status);
        Assert.Equal("foreign", await page.TitleAsync());

        // A page served from no address of its own never gets a request to a loopback host out of
        // Chromium (TlsLoadBalancer.ServeForeign); one served from loopback does, and the browser
        // computes its Fetch Metadata.
        await page.EvaluateAsync("(url) => fetch(url, { credentials: 'include' }).catch(() => null)", app.Url("/bff/session").ToString());
        Exchange sent = Assert.Single(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/bff/session");
        Assert.Equal(fetchSite, sent.Sent("Sec-Fetch-Site"));
        Assert.Equal(url.GetLeftPart(UriPartial.Authority), sent.Sent("Origin"));
    }

    [Fact]
    public async Task A_foreign_page_whose_key_the_browser_was_not_told_to_trust_does_not_load()
    {
        await using ForeignSites foreign = await ForeignSites.StartAsync(LocalStack.Domain, TestContext.Current.CancellationToken);
        await using ForeignSites other = await ForeignSites.StartAsync(LocalStack.Domain, TestContext.Current.CancellationToken);
        await using IBrowser browser = await Chromium.LaunchAsync($"--host-resolver-rules={foreign.ResolverRules}", ForeignSites.TrustSwitch(other.Spki));
        await using IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = false });
        IPage page = await context.NewPageAsync();
        foreign.Serve(ForeignSites.Elsewhere, "<!doctype html><title>foreign</title>");

        PlaywrightException refused = await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync(ForeignSites.Elsewhere.ToString()));
        Assert.Contains("ERR_CERT", refused.Message, StringComparison.Ordinal);
    }
}
