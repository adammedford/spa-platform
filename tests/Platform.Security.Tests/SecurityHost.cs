using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Security.Tests;

/// <summary>
/// Workstream 4's own gateway, cut down to what task 4.6 fills: <c>AddPlatformSecurity</c> and stage
/// 1, <c>UsePlatformForwardedHeaders</c>. Behind them is a terminal that echoes what the later
/// stages would read: the scheme, the client address and the host.
///
/// It listens on loopback, so a test's client address is 127.0.0.1, and the trusted networks are the
/// test's to choose: one that leaves loopback out shows a header from outside being ignored, and one
/// that lists it shows the same header being applied, which is what makes the first assertion able
/// to fail.
///
/// The composed gateway cannot show stage 1 on its own yet: everything after it stops at the stage-4
/// stub until task 2.3. Workstream 6 proves these behaviours again in the composed suites.
/// <c>Platform.Proxy.Tests.Loopback</c> does the same loopback set-up, and a shared helper belongs in
/// <c>Platform.Testing</c>, which is an integrator path.
/// </summary>
public sealed class SecurityHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private SecurityHost(WebApplication app, Uri baseAddress, List<string> log)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Log = log;
    }

    public Uri BaseAddress { get; }

    /// <summary>Every log line the host wrote, formatted, so a test can look for a rule id.</summary>
    public List<string> Log { get; }

    /// <summary>A host whose <c>ForwardedHeaders:TrustedNetworks</c> are the ones given.</summary>
    public static async Task<SecurityHost> StartAsync(CancellationToken ct, params string[] trustedNetworks)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        List<string> log = [];
        builder.Logging.AddProvider(new ListLoggerProvider(log));

        builder.Configuration.AddInMemoryCollection(
            trustedNetworks.Select((n, i) => new KeyValuePair<string, string?>($"ForwardedHeaders:TrustedNetworks:{i}", n)));

        builder.AddPlatformSecurity();

        WebApplication web = builder.Build();
        web.UsePlatformForwardedHeaders();
        web.Run(context => context.Response.WriteAsync(
            $"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}|{context.Request.Host}", ct));

        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        return new SecurityHost(web, address, log);
    }

    /// <summary>
    /// Sends <paramref name="head"/> as written, then a blank line, over a raw socket, and returns
    /// everything the server sent until it closed. A raw socket because <c>HttpClient</c> will not
    /// send an origin server an HTTP/1.1 <c>CONNECT</c>, and an oversized header is plainer this way.
    /// </summary>
    public async Task<string> RawAsync(string head, CancellationToken ct)
    {
        using TcpClient client = new();
        await client.ConnectAsync(BaseAddress.Host, BaseAddress.Port, ct);
        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "\r\n\r\n"), ct);

        using MemoryStream received = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
