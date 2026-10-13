using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy.Tests;

/// <summary>
/// An upstream that accepts the connection and then says nothing. The activity timeout bounds
/// silence rather than the whole exchange (findings T-C17, T-C18), so silence before the response
/// headers is the only thing that produces <c>upstream_timeout</c>, and it needs an upstream that
/// does not answer. The echo upstream always answers.
/// </summary>
public sealed class SilentUpstream : IAsyncDisposable
{
    private readonly WebApplication app;

    private SilentUpstream(WebApplication app, Uri baseAddress)
    {
        this.app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<SilentUpstream> StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();
        app.Run(context => Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));

        await app.StartAsync(ct);

        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the silent upstream started without an address.");

        return new SilentUpstream(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
