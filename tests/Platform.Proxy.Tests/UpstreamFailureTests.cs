using System.Net;
using System.Net.Http.Json;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// The two rows the upstream contract gives for a forward that does not come back: 502
/// <c>upstream_unavailable</c> and 504 <c>upstream_timeout</c>. Both are the activity timeout's
/// doing or the connection's, and task 4.1 is what sets that timeout, so this is where they are
/// proven (findings T-C17, T-C18).
/// </summary>
public sealed class UpstreamFailureTests
{
    [Fact]
    public async Task An_upstream_that_cannot_be_reached_is_502_upstream_unavailable()
    {
        // Port 1 on loopback: nothing listens, so the connection is refused at once.
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, TestApps.Entry("app1", apis: [TestApps.Api("orders", "http://127.0.0.1:1")]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("upstream_unavailable", await CodeOf(response));
    }

    [Fact]
    public async Task An_upstream_that_stays_silent_past_the_entry_timeout_is_504_upstream_timeout()
    {
        await using SilentUpstream silent = await SilentUpstream.StartAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [
                TestApps.Api("orders", silent.BaseAddress.AbsoluteUri, timeoutSeconds: 1),
            ]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("upstream_timeout", await CodeOf(response));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())?.Code;

    private sealed record Problem(int Status, string Code);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
