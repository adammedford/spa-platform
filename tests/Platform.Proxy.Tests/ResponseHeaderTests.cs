using System.Net;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// N-12 and T-C7: what the browser receives from an upstream that sends <c>Set-Cookie</c>, CORS
/// headers and its own policy. No cookie and no <c>Access-Control-*</c>, whatever the upstream's
/// status (<c>contracts/upstream-contract.md</c> -> Returned to the browser).
///
/// T-C7 (with T-C20) is why the status matters: YARP's header removal acts on successful responses
/// only by default, so an upstream's 500 delivered its <c>Set-Cookie</c>.
/// </summary>
public sealed class ResponseHeaderTests
{
    private const string Host = "app1.spa-poc.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(401)]
    public async Task No_cookie_and_no_cors_header_from_the_upstream_reach_the_browser(int status)
    {
        await using PlantingUpstream upstream = await PlantingUpstream.StartAsync(Ct);
        upstream.Status = status;
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, Listing(upstream));

        using HttpResponseMessage response = await Send(gateway);

        // The upstream's own answer, so the absences below are of headers it really sent.
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(PlantingUpstream.Body, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["control"], response.Headers.GetValues("X-Upstream-Marker"));

        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(
            response.Headers.Concat(response.Content.Headers),
            header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task The_browser_receives_the_gateways_policy_and_not_the_upstreams(int status)
    {
        // N-12's third clause. The policy is UsePlatformSecurityHeaders' (task 4.5), which runs ahead of
        // the proxy in the pipeline contract and overwrites what the proxy copied; this host puts it
        // there so the whole row is proven against one response.
        await using PlantingUpstream upstream = await PlantingUpstream.StartAsync(Ct);
        upstream.Status = status;
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, new ProxyHostOptions { SecurityHeaders = true }, Listing(upstream));

        using HttpResponseMessage response = await Send(gateway);

        Assert.Equal(status, (int)response.StatusCode);
        string policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain(PlantingUpstream.PlantedPolicy, policy, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self' " + ProxyHostOptions.BrowserOrigin, policy, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static Platform.Abstractions.AppDefinition Listing(PlantingUpstream upstream) =>
        TestApps.Entry("app1", apis: [TestApps.Api("orders", upstream.BaseAddress.AbsoluteUri)]);

    private static async Task<HttpResponseMessage> Send(ProxyHost gateway)
    {
        using HttpClient client = new(new HttpClientHandler { UseCookies = false, CheckCertificateRevocationList = true });
        return await client.SendAsync(gateway.Request(HttpMethod.Get, Host, "/api/orders/items"), Ct);
    }
}
