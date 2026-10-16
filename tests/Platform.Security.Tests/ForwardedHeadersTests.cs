namespace Platform.Security.Tests;

/// <summary>
/// N-09: <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> from outside the known networks are
/// ignored (threat T6, ADR-0010). The client here is always 127.0.0.1, so a host that lists loopback
/// is the case where the same headers are applied.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Forwarded(Uri baseAddress)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseAddress, "/"));
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        return request;
    }

    [Fact]
    public async Task N09_forwarded_headers_from_outside_the_trusted_networks_are_ignored()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");
        using HttpClient client = new();

        string seen = await (await client.SendAsync(Forwarded(host.BaseAddress), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal($"http|127.0.0.1|{host.BaseAddress.Authority}", seen);
    }

    [Fact]
    public async Task Forwarded_proto_and_for_from_a_trusted_network_are_applied()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "127.0.0.0/8");
        using HttpClient client = new();

        string seen = await (await client.SendAsync(Forwarded(host.BaseAddress), Ct)).Content.ReadAsStringAsync(Ct);

        // The host is unchanged even from a trusted network: X-Forwarded-Host is never read, because
        // host resolution finds the app from the authority (contracts/pipeline.md).
        Assert.Equal($"https|203.0.113.7|{host.BaseAddress.Authority}", seen);
    }

    [Fact]
    public async Task Only_the_nearest_forwarded_address_is_taken()
    {
        // A forward limit of one (T6): the ALB appends the address it saw, so only the last entry is
        // the ALB's; anything before it is whatever the client wrote.
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "127.0.0.0/8");
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, "/"));
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, 203.0.113.7");

        string seen = await (await client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal($"http|203.0.113.7|{host.BaseAddress.Authority}", seen);
    }

    [Fact]
    public async Task With_no_trusted_networks_configured_no_forwarded_header_is_applied()
    {
        // The framework's own defaults trust loopback. They must not come back when the setting is
        // missing.
        await using SecurityHost host = await SecurityHost.StartAsync(Ct);
        using HttpClient client = new();

        string seen = await (await client.SendAsync(Forwarded(host.BaseAddress), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal($"http|127.0.0.1|{host.BaseAddress.Authority}", seen);
    }

    [Fact]
    public async Task A_trusted_network_that_is_not_a_network_stops_the_gateway_starting()
    {
        // Found at start-up, not on the first request after a deployment reports healthy.
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0");
        });
    }
}
