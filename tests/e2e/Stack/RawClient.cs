using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The raw HTTP client of the deployed tier (docs/plan/04-tests.md -> "How the suites run"): a request
/// no browser would send, over TLS to one endpoint whatever the URL's host, so the <c>Host</c> header
/// and the server name are the URL's while the connection is the stand-in load balancer's locally and
/// the tunnel's on the cell. It carries exactly the headers given: no cookies, no Fetch Metadata, no
/// redirects followed.
/// </summary>
public static class RawClient
{
    /// <summary>
    /// A client for <paramref name="endpoint"/> (<c>127.0.0.1:port</c>). Without
    /// <paramref name="validate"/> the certificate is validated as the machine's trust store says, which
    /// is what the cell's needs: it chains to the authority <c>tools/scripts/certs.sh</c> installed.
    /// </summary>
    public static HttpClient To(string endpoint, RemoteCertificateValidationCallback? validate = null)
    {
        string[] parts = endpoint.Split(':');
        IPAddress address = IPAddress.Parse(parts[0]);
        int port = int.Parse(parts[1], CultureInfo.InvariantCulture);
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,

            // "Exactly the headers given": no trace context of the test process's own, which an
            // in-process listener (the local stack's gateway) would otherwise have the handler write
            // over a continuity row's client traceparent (#358 Q137).
            ActivityHeadersPropagator = null,
            ConnectCallback = async (_, ct) =>
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(address, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        if (validate is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = validate;
        }

        return new HttpClient(handler);
    }
}
