using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The raw sender and the one <see cref="RawResponse"/> (#140, #145), over a real socket through
/// <see cref="StageHost.RawAsync(string, string, string, IEnumerable{ValueTuple{string, string}}?, CancellationToken)"/>.
/// Each test names the mutation that turns it red.
///
/// Not tested, deliberately: the tolerance of a reset after the answer. No deterministic way to make
/// Kestrel reset a connection after answering was found, and a test that cannot fail would be a
/// decoration (#140, Testing Decisions).
/// </summary>
public sealed class RawHttpTests
{
    [Fact]
    public async Task RawAsync_sends_the_request_line_as_written()
    {
        // Mutation: route the target through Uri, which removes the dot segment. N-08 sends exactly
        // this kind of target, and a stage must see it as the test wrote it.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
                context.Response.WriteAsync(context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget)),
        }, Ct);

        RawResponse response = await host.RawAsync("GET", "/a/../b", "app1.apps.example.test", ct: Ct);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("/a/../b", response.BodyText);
    }

    [Fact]
    public async Task Repeated_headers_come_back_as_written_in_order()
    {
        // Mutation: collapse the headers into a dictionary. The second Set-Cookie is the value a
        // security test exists to see.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
            {
                context.Response.Headers.Append("Set-Cookie", "first=1");
                context.Response.Headers.Append("Set-Cookie", "second=2");
                return Task.CompletedTask;
            }),
        }, Ct);

        RawResponse response = await host.RawAsync("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", Ct);

        Assert.Equal(["first=1", "second=2"], response.Values("set-cookie"));
        Assert.Equal("first=1, second=2", response.Header("Set-Cookie"));
    }

    [Fact]
    public async Task BodyText_takes_the_chunked_framing_off_and_Body_keeps_it()
    {
        // Mutations: skip the de-chunking, and BodyText carries the size lines; de-chunk Body too,
        // and a test about the wire loses the framing. Kestrel chunks a body written with no
        // Content-Length, as the gateway's problem documents are.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(async context =>
            {
                await context.Response.WriteAsync("{\"code\":", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await context.Response.WriteAsync("\"chunked\"}", context.RequestAborted);
            }),
        }, Ct);

        RawResponse response = await host.RawAsync("GET", "/", "localhost", ct: Ct);

        Assert.Equal("chunked", response.Header("Transfer-Encoding"));
        Assert.Equal("{\"code\":\"chunked\"}", response.BodyText);
        Assert.EndsWith("\r\n0\r\n\r\n", Encoding.ASCII.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyText_refuses_chunked_framing_cut_off_before_its_last_chunk()
    {
        // Mutation: return what was decoded when the framing ends early. A test that parsed part of a
        // problem document as the whole would pass or fail on bytes the server never finished
        // sending. Kestrel frames every body it sends, so the terminal here is a bare listener that
        // writes one chunk and closes without the last.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task serve = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
            NetworkStream stream = client.GetStream();
            byte[] request = new byte[4096];
            int read = 0;
            while (request.AsSpan(0, read).IndexOf("\r\n\r\n"u8) < 0)
            {
                read += await stream.ReadAsync(request.AsMemory(read), Ct);
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n7\r\npartial\r\n"u8.ToArray(), Ct);
            client.Client.Shutdown(SocketShutdown.Send);
        }, Ct);

        Uri endpoint = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        RawResponse response = await RawHttp.SendAsync(endpoint, "GET", "/", "localhost", ct: Ct);
        await serve;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("7\r\npartial\r\n", Encoding.ASCII.GetString(response.Body));
        Assert.Throws<InvalidDataException>(() => response.BodyText);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
