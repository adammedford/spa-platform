using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Platform.Proxy.Tests;

/// <summary>
/// Where an upstream falls silent, which picks the upstream contract's case ("Timeouts and
/// failures"): before its headers is 504 <c>upstream_timeout</c>, after its headers and before a
/// body byte is 502 <c>upstream_unavailable</c>, and after a body byte the call ends with no problem
/// body.
/// </summary>
public enum Silence
{
    /// <summary>Accepts the connection and sends nothing at all.</summary>
    BeforeHeaders,

    /// <summary>Starts its response and flushes, as the T-C18 spike's <c>/stall</c> does, and sends no body byte.</summary>
    AfterHeaders,

    /// <summary>Writes one body byte, <see cref="SilentUpstream.FirstByte"/>, flushes, and sends nothing more.</summary>
    AfterFirstByte,
}

/// <summary>
/// An upstream that accepts the connection and then says nothing, from the point <see cref="Silence"/>
/// names. The activity timeout bounds silence rather than the whole exchange (findings T-C17, T-C18),
/// so the place the silence starts is what picks the answer, and it needs an upstream that stops
/// answering there. The echo upstream always answers.
/// </summary>
public sealed class SilentUpstream : IAsyncDisposable
{
    /// <summary>The status an upstream that sends its headers answers with: one the gateway never sets itself.</summary>
    public const int Status = StatusCodes.Status202Accepted;

    /// <summary>The media type an upstream that sends its headers declares.</summary>
    public const string MediaType = "text/plain";

    /// <summary>The one body byte <see cref="Silence.AfterFirstByte"/> sends, a single ASCII character.</summary>
    public const string FirstByte = "x";

    private readonly WebApplication app;

    private SilentUpstream(WebApplication app, Uri baseAddress)
    {
        this.app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<SilentUpstream> StartAsync(CancellationToken ct) =>
        await StartAsync(Silence.BeforeHeaders, ct);

    public static async Task<SilentUpstream> StartAsync(Silence silence, CancellationToken ct)
    {
        WebApplicationBuilder builder = Loopback.Builder();

        WebApplication app = builder.Build();
        app.Run(async context =>
        {
            if (silence != Silence.BeforeHeaders)
            {
                context.Response.StatusCode = Status;
                context.Response.ContentType = MediaType;
                await context.Response.StartAsync(context.RequestAborted);

                if (silence == Silence.AfterFirstByte)
                {
                    await context.Response.WriteAsync(FirstByte, context.RequestAborted);
                }

                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });

        await app.StartAsync(ct);

        return new SilentUpstream(app, Loopback.AddressOf(app, "silent upstream"));
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
