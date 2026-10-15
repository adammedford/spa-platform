using Gateway.TestHarness;
using Platform.Testing;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// The stand-in for the ALB, pointed at an echo upstream in place of the gateway so what it forwards can
/// be read off the wire.
/// </summary>
public sealed class LoadBalancerTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_name_is_forwarded_as_the_alb_forwards_it()
    {
        EchoUpstream echo = new();
        await using EchoUpstreamListener listener = await echo.ListenAsync(Ct);
        using TestBrowser browser = new(new LoadBalancerTransport(listener.BaseAddress, clientAddress: "198.51.100.7"));

        await browser.NavigateAsync("https://app1.spa-poc.test:8443/a/b?c=d", RequestOptions.With(
            ("X-Forwarded-For", "203.0.113.9"),
            ("X-Forwarded-Proto", "http"),
            ("X-Forwarded-Host", "app2.spa-poc.test:8443")), Ct);

        EchoedRequest received = Assert.Single(echo.Requests);
        Assert.Equal("/a/b", received.Path);
        Assert.Equal("?c=d", received.Query);
        Assert.Equal("app1.spa-poc.test:8443", received.Headers["Host"].Single());

        // Overwritten, appended, and passed through untouched, in that order.
        Assert.Equal("https", received.Headers["X-Forwarded-Proto"].Single());
        Assert.Equal("8443", received.Headers["X-Forwarded-Port"].Single());
        Assert.Equal("203.0.113.9, 198.51.100.7", string.Join(", ", received.Headers["X-Forwarded-For"]));
        Assert.Equal("app2.spa-poc.test:8443", received.Headers["X-Forwarded-Host"].Single());
        Assert.Equal(FetchMetadata.None, received.Headers["Sec-Fetch-Site"].Single());
    }

    [Fact]
    public async Task A_loopback_address_is_reached_directly()
    {
        EchoUpstream echo = new();
        await using EchoUpstreamListener listener = await echo.ListenAsync(Ct);
        using TestBrowser browser = new(new LoadBalancerTransport(new Uri("http://127.0.0.1:1/")));

        await browser.NavigateAsync(new Uri(listener.BaseAddress, "/direct"), ct: Ct);

        EchoedRequest received = Assert.Single(echo.Requests);
        Assert.Equal("/direct", received.Path);
        Assert.False(received.Headers.ContainsKey("X-Forwarded-Proto"));
    }
}
