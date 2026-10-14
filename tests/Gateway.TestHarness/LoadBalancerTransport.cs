using System.Net;

namespace Gateway.TestHarness;

/// <summary>
/// The test browser's network: a stand-in for the internal ALB in front of the gateway, and a direct
/// line to everything on loopback.
///
/// <para>
/// A request for a name (<c>https://app1.spa-poc.test:8443/</c>, or any unknown host) goes to the
/// gateway's loopback listener the way the ALB sends it: over plain HTTP, with the <c>Host</c> header
/// the browser sent, <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Port</c> set from the browser's URL and
/// the client address appended to <c>X-Forwarded-For</c>. Like the ALB, it overwrites the scheme and
/// port headers, appends to <c>X-Forwarded-For</c> and passes <c>X-Forwarded-Host</c> through untouched
/// (https://docs.aws.amazon.com/elasticloadbalancing/latest/application/x-forwarded-headers.html), so a
/// spoofed one reaches the gateway as it would in the cell (N-08).
/// </para>
/// <para>
/// A request for an IP address or <c>localhost</c> goes straight there: the test identity provider, the
/// mock API and the echo upstream all listen on loopback, which is where a login redirect sends the
/// browser.
/// </para>
/// <para>
/// No proxy is used, whatever the environment says: in an agent sandbox HTTP is proxied and answered on
/// the <c>Host</c> header, so a spoofed host would never reach the process (AGENTS.md).
/// </para>
/// </summary>
public sealed class LoadBalancerTransport : HttpMessageHandler
{
    private readonly Uri gateway;
    private readonly string clientAddress;
    private readonly HttpMessageInvoker network;

    /// <param name="gateway">The gateway's listener, e.g. <c>http://127.0.0.1:54321/</c>.</param>
    /// <param name="clientAddress">The browser's address as the ALB reports it in <c>X-Forwarded-For</c>.</param>
    public LoadBalancerTransport(Uri gateway, string clientAddress = "198.51.100.7")
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.clientAddress = clientAddress;
        network = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        });
    }

    /// <summary>Whether a URL is served by the gateway (any name) rather than by a loopback listener.</summary>
    public static bool IsGatewayUrl(Uri url) =>
        url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) && !url.IsLoopback;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uri url = request.RequestUri ?? throw new InvalidOperationException("the request has no URI.");
        if (!IsGatewayUrl(url))
        {
            return network.SendAsync(request, ct);
        }

        // The authority as the browser sends it in Host: the port only when it is not the scheme's default.
        // A test that set Host itself keeps its own.
        request.Headers.Host ??= url.IsDefaultPort ? url.IdnHost : $"{url.IdnHost}:{url.Port}";
        request.RequestUri = new Uri(gateway, url.PathAndQuery);

        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.Remove("X-Forwarded-Port");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", url.Scheme);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Port", url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string forwardedFor = request.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? existing)
            ? string.Join(", ", existing) + ", " + clientAddress
            : clientAddress;
        request.Headers.Remove("X-Forwarded-For");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);

        return network.SendAsync(request, ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            network.Dispose();
        }

        base.Dispose(disposing);
    }
}
