using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Security.Tests;

/// <summary>
/// N-20: <c>TRACE</c>, <c>CONNECT</c> and oversized headers give 405 or 431. The unsafe method on an
/// asset path is the request gate's per-path rule (<c>contracts/request-gate.md</c>, rule
/// <c>gate.method</c>, task 4.3); stage 1 refuses the methods no path accepts.
/// </summary>
public sealed class RequestLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("TRACE")]
    [InlineData("TRACK")]
    [InlineData("PROPFIND")]
    public async Task N20_a_method_no_path_accepts_gets_405_method_not_allowed(string method)
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), new Uri(host.BaseAddress, "/api/orders/items")), Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(405, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("method_not_allowed", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], response.Content.Headers.Allow);

        // The rule id is in the refusal's log line; the method itself is not, because the client
        // chose it. (The framework's own request log names it; task 6.4 replaces that log.)
        lock (host.Log)
        {
            string refusal = Assert.Single(host.Log, line => line.Contains("rule=gate.method"));
            Assert.DoesNotContain(method, refusal);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task A_method_some_path_accepts_passes_stage_1(string method)
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), new Uri(host.BaseAddress, "/api/orders/items")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task N20_connect_gets_405()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");

        string response = await host.RawAsync(
            $"CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nConnection: close", Ct);

        Assert.StartsWith("HTTP/1.1 405 ", response);
    }

    [Fact]
    public async Task N20_headers_past_the_configured_total_get_431_with_no_body()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");

        // One byte past the configured total, in a single header.
        string response = await host.RawAsync(
            $"GET / HTTP/1.1\r\nHost: {host.BaseAddress.Authority}\r\nX-Big: {new string('a', PlatformSecurityLimits.MaxRequestHeadersTotalSize)}", Ct);

        Assert.StartsWith("HTTP/1.1 431 ", response);
        Assert.Contains("Content-Length: 0", response);
    }

    [Fact]
    public async Task N20_more_headers_than_the_configured_count_get_431()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");

        string many = string.Join("\r\n",
            Enumerable.Range(0, PlatformSecurityLimits.MaxRequestHeaderCount + 1).Select(i => $"X-H{i}: v"));
        string response = await host.RawAsync(
            $"GET / HTTP/1.1\r\nHost: {host.BaseAddress.Authority}\r\n{many}", Ct);

        Assert.StartsWith("HTTP/1.1 431 ", response);
    }

    [Fact]
    public async Task Headers_within_the_configured_total_are_accepted()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(Ct, "10.20.0.0/16");

        string response = await host.RawAsync(
            $"GET / HTTP/1.1\r\nHost: {host.BaseAddress.Authority}\r\nConnection: close\r\nX-Big: {new string('a', PlatformSecurityLimits.MaxRequestHeadersTotalSize - 1024)}", Ct);

        Assert.StartsWith("HTTP/1.1 200 ", response);
    }

    [Fact]
    public void AddPlatformSecurity_applies_the_limits_to_kestrel()
    {
        // The socket tests above bracket the configured values, but those values are Kestrel's own
        // defaults, so they would pass with the limits never applied. Here every limit starts
        // somewhere else, and only AddPlatformSecurity can bring it back.
        Microsoft.AspNetCore.Builder.WebApplicationBuilder builder =
            Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestHeadersTotalSize = 1;
            options.Limits.MaxRequestHeaderCount = 1;
            options.Limits.MaxRequestLineSize = 1;
            options.Limits.MaxRequestBodySize = 1;
        });

        builder.AddPlatformSecurity();

        using ServiceProvider services = builder.Services.BuildServiceProvider();
        var limits = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>()
            .Value.Limits;
        Assert.Equal(PlatformSecurityLimits.MaxRequestHeadersTotalSize, limits.MaxRequestHeadersTotalSize);
        Assert.Equal(PlatformSecurityLimits.MaxRequestHeaderCount, limits.MaxRequestHeaderCount);
        Assert.Equal(PlatformSecurityLimits.MaxRequestLineSize, limits.MaxRequestLineSize);
        Assert.Equal(PlatformSecurityLimits.MaxRequestBodySize, limits.MaxRequestBodySize);
    }
}
