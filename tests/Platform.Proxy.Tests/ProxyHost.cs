using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Workstream 4's own gateway, cut down to the two entry points the pipeline contract gives task
/// 4.1: <c>AddPlatformProxy</c> and <c>MapPlatformProxy</c>. No other stage is here, because no
/// other stage is this task's, and the composed suites in workstream 6 run the whole pipeline.
///
/// It listens on loopback and the test sets the <c>Host</c> header itself. Everything here routes by
/// host, so a host a client cannot choose would prove nothing.
///
/// The composed gateway cannot show any of this yet: the host-resolution stub at stage 4 answers 404
/// to every host until task 2.3, so nothing after it runs. Workstream 6 proves these behaviours again
/// in the composed suites once it does.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private ProxyHost(WebApplication app, Uri baseAddress, InMemoryRegistry registry)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Registry = registry;
    }

    /// <summary>Where the gateway is listening. The <c>Host</c> header is the test's to set.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The registry the routes come from. Swap it to move the routes on.</summary>
    public InMemoryRegistry Registry { get; }

    public static async Task<ProxyHost> StartAsync(CancellationToken ct, params AppDefinition[] apps)
    {
        InMemoryRegistry registry = new(InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps));

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRegistry>(registry);
        builder.AddPlatformProxy();

        WebApplication web = builder.Build();

        // As Program.cs does it: stage 7 is the framework's own UseRouting, and the Map entry points
        // of stage 10 come after it.
        web.UseRouting();
        web.MapPlatformProxy();

        await web.StartAsync(ct);

        string address = web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the gateway started without an address.");

        return new ProxyHost(web, new Uri(address), registry);
    }

    /// <summary>A request to this gateway, addressed to one app's host.</summary>
    public HttpRequestMessage Request(HttpMethod method, string host, string pathAndQuery)
    {
        HttpRequestMessage request = new(method, new Uri(BaseAddress, pathAndQuery));
        request.Headers.Host = host;
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
