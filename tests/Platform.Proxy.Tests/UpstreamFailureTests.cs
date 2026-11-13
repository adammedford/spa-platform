using System.Net;
using System.Text;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// The cases the upstream contract gives for a forward that does not come back ("Timeouts and
/// failures"): 502 <c>upstream_unavailable</c> when the upstream cannot be reached or falls silent
/// after its headers, 504 <c>upstream_timeout</c> when it falls silent before them, and no problem
/// body once a body byte has been forwarded. Each is the activity timeout's doing or the
/// connection's, and task 4.1 is what sets that timeout, so this is where they are proven (findings
/// T-C17, T-C18).
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
        Assert.Equal("upstream_unavailable", await Problem.CodeOf(response));
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
        Assert.Equal("upstream_timeout", await Problem.CodeOf(response));
    }

    [Fact]
    public async Task An_upstream_that_sends_its_headers_and_then_stays_silent_is_502_upstream_unavailable()
    {
        await using SilentUpstream silent = await SilentUpstream.StartAsync(Silence.AfterHeaders, Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [
                TestApps.Api("orders", silent.BaseAddress.AbsoluteUri, timeoutSeconds: 1),
            ]));
        using HttpClient client = new();

        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("upstream_unavailable", await Problem.CodeOf(response));
    }

    [Fact]
    public async Task An_upstream_that_sends_a_body_byte_and_then_stays_silent_keeps_its_status_and_the_read_ends_in_a_connection_error()
    {
        await using SilentUpstream silent = await SilentUpstream.StartAsync(Silence.AfterFirstByte, Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [
                TestApps.Api("orders", silent.BaseAddress.AbsoluteUri, timeoutSeconds: 1),
            ]));
        using HttpClient client = new();

        // Without ResponseHeadersRead the client buffers the body, the read fails, and the status is
        // never seen.
        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"),
            HttpCompletionOption.ResponseHeadersRead,
            Ct);

        Assert.Equal(SilentUpstream.Status, (int)response.StatusCode);
        Assert.Equal(SilentUpstream.MediaType, response.Content.Headers.ContentType?.MediaType);

        // The bound is well past the entry's one second, so a gateway that held the call open would
        // end it in cancellation, which is not an IOException.
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(10));
        await using Stream body = await response.Content.ReadAsStreamAsync(bound.Token);
        byte[] buffer = new byte[64];

        int read = await body.ReadAsync(buffer, bound.Token);
        Assert.Equal(SilentUpstream.FirstByte, Encoding.ASCII.GetString(buffer, 0, read));

        // Observed: the gateway resets the connection once the idle timeout fires, so the next read
        // fails rather than reporting an early end of the stream (which would return 0 here, not throw),
        // and nothing follows the byte.
        await Assert.ThrowsAnyAsync<IOException>(() =>
            body.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, bound.Token).AsTask());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
