using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// An upstream that answers with exactly the headers an upstream is not allowed to reach the browser
/// with: a <c>Set-Cookie</c>, a <c>Access-Control-Allow-*</c> pair and a content security policy of
/// its own (<c>contracts/upstream-contract.md</c> -> "Returned to the browser ... minus
/// <c>Set-Cookie</c> and <c>Access-Control-*</c>. The gateway's security headers replace the
/// upstream's.").
///
/// <para><b>Why not the harness's echo upstream.</b> The echo records what reached it and can be
/// given a status and a body (<c>EchoResponse</c>), but not headers of its own, and this row is
/// about nothing else. <b>Why a bare socket rather than a small web app:</b> the bytes are the
/// input. A server framework would normalise a header set written to provoke the gateway, and this
/// writes the response line and the headers itself for the same reason <c>RawHttp</c> does on the
/// other side.</para>
///
/// <para>It speaks the little of HTTP/1.1 that YARP needs of an origin server: it reads the request
/// head, ignores any body, answers with <c>Content-Length</c> and <c>Connection: close</c>, and
/// closes. One connection per request, so no keep-alive state to get wrong.</para>
/// </summary>
internal sealed class HostileUpstream : IAsyncDisposable
{
    /// <summary>The cookie it tries to plant in the browser.</summary>
    public const string PlantedCookie = "upstream-planted";

    /// <summary>The policy it tries to have the browser apply instead of the app's.</summary>
    public const string PlantedPolicy = "default-src *; script-src 'unsafe-inline' *";

    /// <summary>The origin it tries to grant a cross-origin read to.</summary>
    public const string PlantedCorsOrigin = "https://evil.test";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task serving;

    private HostileUpstream(TcpListener listener)
    {
        this.listener = listener;
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        serving = ServeAsync();
    }

    /// <summary>Where it listens, as a registry <c>upstream</c> names it.</summary>
    public Uri Address { get; }

    /// <summary>The status it answers with. A test sets 500 to read the row's "and on a 500" half.</summary>
    public int Status { get; set; } = 200;

    /// <summary>The body it answers with, so a test can tell its answer from the gateway's.</summary>
    public string Body { get; set; } = "the upstream answered";

    public static HostileUpstream Start()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return new HostileUpstream(listener);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();

        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
        }

        stopping.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            // One at a time: a test makes one call and reads one answer, and serialising keeps the
            // failure of a connection attributable to the request that made it.
            using (client)
            {
                try
                {
                    await AnswerAsync(client, stopping.Token);
                }
                catch (IOException)
                {
                    // The caller gave up on the connection. The next one is unaffected.
                }
            }
        }
    }

    private async Task AnswerAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();

        // Read the request head and nothing more. Anything after it is a body this upstream has no
        // use for, and leaving it unread is fine on a connection that is about to close.
        byte[] buffer = new byte[8192];
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (got == 0 || buffer.AsSpan(0, read += got).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        byte[] body = Encoding.UTF8.GetBytes(Body);
        StringBuilder head = new();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {Status} Upstream\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n");
        head.Append("Content-Type: text/plain\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Set-Cookie: {PlantedCookie}=planted; Path=/\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Access-Control-Allow-Origin: {PlantedCorsOrigin}\r\n");
        head.Append("Access-Control-Allow-Credentials: true\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Security-Policy: {PlantedPolicy}\r\n");
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
