using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

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
        WebApplicationBuilder builder = Loopback.Builder();

        WebApplication app = builder.Build();
        app.Run(context => Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));

        await app.StartAsync(ct);

        return new SilentUpstream(app, Loopback.AddressOf(app, "silent upstream"));
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
