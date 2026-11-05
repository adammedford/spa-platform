using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Platform.Identity.Tests;

/// <summary>The three ways #908 names in which a provider's discovery document cannot be fetched.</summary>
public enum DiscoveryOutage
{
    /// <summary>Nothing listens on the authority's port, so the connection is refused.</summary>
    Refused,

    /// <summary>The server accepts the connection and never answers, past the backchannel's timeout.</summary>
    Silent,

    /// <summary>The server answers <c>503</c>.</summary>
    Failing,
}

/// <summary>
/// A loopback authority whose discovery document cannot be fetched, in one of the three ways #908
/// names: the connection is refused, the server accepts and never answers, or it answers <c>503</c>.
/// <see cref="Body"/> is what the failing server's answer carries, a canary a log line must not hold.
/// </summary>
internal sealed class UnreachableIssuer : IAsyncDisposable
{
    /// <summary>The body of the <c>503</c> answer: a log line that repeats the provider's answer would hold it.</summary>
    public const string Body = "canary-provider-body-7d41b2";

    private readonly TcpListener? listener;
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> held = [];
    private readonly Task serving = Task.CompletedTask;

    private UnreachableIssuer(DiscoveryOutage way)
    {
        TcpListener bound = new(IPAddress.Loopback, 0);
        bound.Start();
        int port = ((IPEndPoint)bound.LocalEndpoint).Port;
        Authority = new Uri($"http://127.0.0.1:{port}/");
        if (way == DiscoveryOutage.Refused)
        {
            // Released straight away: nothing listens on the port, so a connection to it is refused.
            bound.Stop();
            bound.Dispose();
            return;
        }

        listener = bound;
        serving = ServeAsync(way, stop.Token);
    }

    public Uri Authority { get; }

    /// <summary>How many requests the server read: zero for a refused port.</summary>
    public int Requests { get; private set; }

    public static UnreachableIssuer Start(DiscoveryOutage way) => new(way);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener?.Stop();
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        foreach (TcpClient client in held)
        {
            client.Dispose();
        }

        listener?.Dispose();
        stop.Dispose();
    }

    private async Task ServeAsync(DiscoveryOutage way, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client = await listener!.AcceptTcpClientAsync(token);
            held.Add(client);
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[8192];
            // The request line and headers arrive in one read on loopback; nothing here needs more.
            _ = await stream.ReadAsync(buffer, token);
            Requests++;
            if (way == DiscoveryOutage.Silent)
            {
                continue;
            }

            byte[] answer = Encoding.ASCII.GetBytes(
                "HTTP/1.1 503 Service Unavailable\r\nContent-Type: text/plain\r\nConnection: close\r\n"
                + $"Content-Length: {Body.Length}\r\n\r\n{Body}");
            await stream.WriteAsync(answer, token);
            client.Dispose();
        }
    }
}
