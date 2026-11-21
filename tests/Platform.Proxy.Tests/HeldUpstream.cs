using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy.Tests;

/// <summary>
/// An upstream that answers every request with its headers at once and then a body byte every
/// <see cref="Drip"/>, until <see cref="Release"/>, so each request holds its connection for as long
/// as the test likes. The drip is what keeps it held: the proxy's idle limit restarts on every body
/// byte (T-C17, T-C18), so a held call never times out and frees its connection while the call over
/// the bound is waiting for one (#738). <see cref="SilentUpstream"/> cannot do this: its calls time
/// out too, and the first to go frees a slot.
///
/// It counts connections, not requests, in Kestrel's connection middleware, and records each
/// request's path and protocol, so a test can tell the call over the bound from the held ones and can
/// assert that the forward was HTTP/1.1.
/// </summary>
public sealed class HeldUpstream : IAsyncDisposable
{
    /// <summary>A header only this upstream sets, so a response that carries it came from here.</summary>
    public const string MarkerHeader = "X-Held-Upstream";

    /// <summary>The interval between body bytes: far inside any idle limit a test here sets.</summary>
    public static readonly TimeSpan Drip = TimeSpan.FromMilliseconds(100);

    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> paths = new();
    private readonly ConcurrentQueue<string> protocols = new();
    private WebApplication? app;
    private int current;
    private int peak;

    private HeldUpstream()
    {
    }

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Connections open to this upstream now.</summary>
    public int CurrentConnections => Volatile.Read(ref current);

    /// <summary>The most connections that were ever open to this upstream at once.</summary>
    public int PeakConnections => Volatile.Read(ref peak);

    /// <summary>The path of every request that reached this upstream, as it arrived.</summary>
    public IReadOnlyCollection<string> Paths => [.. paths];

    /// <summary>The protocol of every request that reached this upstream, as Kestrel read it.</summary>
    public IReadOnlyCollection<string> Protocols => [.. protocols];

    public static async Task<HeldUpstream> StartAsync(CancellationToken ct)
    {
        HeldUpstream upstream = new();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // The drip is a byte at a time, far under Kestrel's minimum response rate; the client is
            // the proxy, which reads every byte, but the rate is not this double's subject.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Use(upstream.Count));
        });

        WebApplication app = builder.Build();
        app.Run(upstream.HoldAsync);
        await app.StartAsync(ct);

        upstream.app = app;
        upstream.BaseAddress = Loopback.AddressOf(app, "held upstream");
        return upstream;
    }

    /// <summary>
    /// Waits until <paramref name="connections"/> connections are open at once, polling, under the
    /// caller's bound: a bound that runs out is an <see cref="OperationCanceledException"/>, not a
    /// wait that goes on.
    /// </summary>
    public async Task WaitForConnectionsAsync(int connections, CancellationToken ct)
    {
        while (CurrentConnections < connections)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <summary>Ends every held response, and every one that arrives afterwards at once.</summary>
    public void Release() => released.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        Release();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private ConnectionDelegate Count(ConnectionDelegate next) => async connection =>
    {
        int now = Interlocked.Increment(ref current);
        int seen;
        while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
        {
        }

        try
        {
            await next(connection);
        }
        finally
        {
            Interlocked.Decrement(ref current);
        }
    };

    private async Task HoldAsync(HttpContext context)
    {
        paths.Enqueue(context.Request.Path.Value ?? string.Empty);
        protocols.Enqueue(context.Request.Protocol);

        CancellationToken aborted = context.RequestAborted;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain";
        context.Response.Headers[MarkerHeader] = "held";
        await context.Response.StartAsync(aborted);

        while (!released.Task.IsCompleted && !aborted.IsCancellationRequested)
        {
            await context.Response.WriteAsync(".", aborted);
            await context.Response.Body.FlushAsync(aborted);
            await Task.WhenAny(released.Task, Task.Delay(Drip, aborted));
        }
    }
}
