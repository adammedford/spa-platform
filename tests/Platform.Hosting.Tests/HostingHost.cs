using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// Workstream 2's gateway cut down to stages 3 and 4, <c>UsePlatformHealth</c> then
/// <c>UsePlatformHostResolution</c>, over an <see cref="InMemoryRegistry"/> the test swaps by hand.
/// The composed gateway cannot run either against a registry until task 2.2's source has a bucket,
/// so the stages are proven here and again by workstream 6.
///
/// The terminal stands for stages 5 to 10. It records the <see cref="IAppFeature"/> it was handed and
/// answers 200 with <see cref="Passed"/>, so a test that asserts a refusal can fail if stage 4 let the
/// request through. A test can give it work to do mid-request through <see cref="DuringRequest"/>.
///
/// Given an <see cref="IObjectStore"/>, the terminal is stage 10's asset endpoint instead:
/// <c>UseRouting</c> then <c>MapPlatformAssets</c> over that store (task 2.4). Stages 5 to 9 are other
/// workstreams' and are left out, so what the endpoint refuses it refuses on its own.
/// </summary>
public sealed class HostingHost : IAsyncDisposable
{
    public const string Passed = "passed";

    private readonly WebApplication web;
    private int reached;

    private HostingHost(WebApplication web, Uri baseAddress, InMemoryRegistry registry)
    {
        this.web = web;
        BaseAddress = baseAddress;
        Registry = registry;
    }

    public Uri BaseAddress { get; }

    public InMemoryRegistry Registry { get; }

    /// <summary>How many requests reached the terminal, which stands for every stage after 4.</summary>
    public int Reached => Volatile.Read(ref reached);

    /// <summary>The feature the terminal last saw, read when it saw it.</summary>
    public (string AppId, string AppHost, long Revision)? LastSeen { get; private set; }

    /// <summary>Runs in the terminal before it reads the feature: a swap mid-request goes here.</summary>
    public Action? DuringRequest { get; set; }

    public static async Task<HostingHost> StartAsync(RegistrySnapshot initial, CancellationToken ct, IObjectStore? store = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        InMemoryRegistry registry = new(initial);
        builder.Services.AddSingleton<IRegistry>(registry);
        if (store is not null)
        {
            builder.Services.AddSingleton(store);
        }

        builder.AddPlatformHosting();

        WebApplication web = builder.Build();
        HostingHost? self = null;

        web.UsePlatformHealth();
        web.UsePlatformHostResolution();

        if (store is not null)
        {
            // Explicit, and after stage 4 as in src/Gateway/Program.cs: a WebApplication with endpoints
            // and no UseRouting call puts routing at the front of the pipeline, before stages 3 and 4.
            web.UseRouting();
            web.MapPlatformAssets();
            return await StartedAsync(web, registry, ct);
        }

        web.Run(context =>
        {
            Interlocked.Increment(ref self!.reached);
            self.DuringRequest?.Invoke();
            IAppFeature? feature = context.Features.Get<IAppFeature>();
            self.LastSeen = feature is null ? null : (feature.App.AppId, feature.App.Host, feature.Snapshot.Revision);
            return context.Response.WriteAsync(Passed, ct);
        });

        self = await StartedAsync(web, registry, ct);
        return self;
    }

    private static async Task<HostingHost> StartedAsync(WebApplication web, InMemoryRegistry registry, CancellationToken ct)
    {
        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        return new HostingHost(web, address, registry);
    }

    /// <summary>
    /// Sends <c>GET path</c> with the <c>Host</c> header given and any extra header lines, over a raw
    /// socket, and returns the response as text. Raw because the <c>Host</c> header is the subject
    /// (AGENTS.md: reach the gateway over a raw socket when the Host header matters), and
    /// <see cref="HttpClient"/> writes it from the URI.
    /// </summary>
    public Task<RawResponse> GetAsync(string host, string path, CancellationToken ct, string extraHeaders = "") =>
        SendAsync("GET", host, path, ct, extraHeaders);

    /// <summary>
    /// <see cref="GetAsync"/> with any method. The request line is sent byte for byte as given, so a
    /// test can put a dot segment, a backslash or a NUL in it that no HTTP client would send.
    /// </summary>
    public async Task<RawResponse> SendAsync(string method, string host, string path, CancellationToken ct, string extraHeaders = "")
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(BaseAddress.Host, BaseAddress.Port, ct);
        NetworkStream stream = tcp.GetStream();

        string head = $"{method} {path} HTTP/1.1\r\nHost: {host}\r\n{extraHeaders}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);

        using MemoryStream received = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return RawResponse.Parse(Encoding.UTF8.GetString(received.ToArray()));
    }

    public async ValueTask DisposeAsync()
    {
        await web.StopAsync();
        await web.DisposeAsync();
    }
}

/// <summary>A response read off the socket: the status, the headers by lowercase name, the body.</summary>
public sealed record RawResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public static RawResponse Parse(string text)
    {
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? text : text[..split];
        string body = split < 0 ? "" : text[(split + 4)..];

        string[] lines = head.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        // Kestrel chunks an HTTP/1.1 body it was not given a length for (RFC 9112, section 6.1); one
        // chunk is all these bodies need.
        if (headers.TryGetValue("transfer-encoding", out string? encoding) && encoding == "chunked")
        {
            body = Unchunk(body);
        }

        return new RawResponse(status, headers, body);
    }

    private static string Unchunk(string chunked)
    {
        StringBuilder body = new();
        int at = 0;
        while (true)
        {
            int end = chunked.IndexOf("\r\n", at, StringComparison.Ordinal);
            int size = Convert.ToInt32(chunked[at..end], 16);
            if (size == 0)
            {
                return body.ToString();
            }

            body.Append(chunked, end + 2, size);
            at = end + 2 + size + 2;
        }
    }
}
