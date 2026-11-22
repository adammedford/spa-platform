using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy.Tests;

/// <summary>
/// An upstream that holds every request it is sent, and counts the connections it has open, for the
/// HTTP/1.1 connection bound of one app's API (#738; #715 Q7).
///
/// Each request is answered at once with 200 and <see cref="MarkerHeader"/>, flushed, and then one
/// flushed body byte every <see cref="Beat"/> until <see cref="Release"/>, when it ends with
/// <see cref="EndOfBody"/>. The beat is what keeps a held request alive: the entry's
/// <c>timeoutSeconds</c> is an idle limit that the proxy restarts on every body read (T-C17), so a
/// held request that fell silent would time out and free its connection, and the bound would never
/// be reached. <see cref="SilentUpstream"/> stays the double for silence.
///
/// Connections are counted in Kestrel's connection middleware, below HTTP, so a connection the
/// proxy's pool opens is counted whether or not a request ever arrives on it (#738 Q4). Every open,
/// close, arrival and end is kept on a timeline with its connection id, so a test can check that the
/// same identified requests stayed held on distinct connections throughout, and can print the
/// timeline when they did not (#738 Q4, Q11).
/// </summary>
public sealed class HoldingUpstream : IAsyncDisposable
{
    /// <summary>A header on every response this upstream sends, so a response that carries it came from here.</summary>
    public const string MarkerHeader = "X-Holding-Upstream";

    /// <summary>The body byte each beat writes.</summary>
    public const string BeatByte = ".";

    /// <summary>What a released response's body ends with, so a test can tell a whole body from a cut one.</summary>
    public const string EndOfBody = "released";

    /// <summary>How often a held request is sent a body byte: eight beats in a two-second idle limit (#738 Q6).</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(250);

    /// <summary>How long disposal waits for the host to stop, so a stuck handler cannot hold a test's clean-up.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Event> timeline = new();
    private readonly ConcurrentDictionary<string, string> active = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Task> handlers = new();
    private WebApplication? app;
    private int open;
    private int peak;

    private HoldingUpstream()
    {
    }

    /// <summary>What happened on this upstream, and when.</summary>
    public enum Kind
    {
        /// <summary>A connection was accepted.</summary>
        Opened,

        /// <summary>A connection closed.</summary>
        Closed,

        /// <summary>A request arrived and is now held.</summary>
        Arrived,

        /// <summary>A held request ended after <see cref="Release"/>: its body whole, or cut by an abort or a failed write after the release, which a proof tells apart by the body it reads.</summary>
        Ended,

        /// <summary>A held request ended before <see cref="Release"/>: aborted, or its write failed.</summary>
        EndedEarly,
    }

    /// <summary>One entry of the timeline: a monotonic time since start, the connection, and for a request its path and protocol.</summary>
    public sealed record Event(TimeSpan At, Kind Kind, string ConnectionId, string? Path = null, string? Protocol = null);

    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1:1/");

    /// <summary>The connections open to this upstream now.</summary>
    public int OpenConnections => Volatile.Read(ref open);

    /// <summary>The most connections that were ever open to this upstream at once.</summary>
    public int PeakConnections => Volatile.Read(ref peak);

    /// <summary>Each request held now, by its path, with the connection it is held on.</summary>
    public IReadOnlyDictionary<string, string> Held => new Dictionary<string, string>(active, StringComparer.Ordinal);

    /// <summary>Everything that happened, in order.</summary>
    public IReadOnlyList<Event> Timeline => [.. timeline];

    /// <summary>Every request that reached this upstream, in the order it arrived.</summary>
    public IReadOnlyList<Event> Requests => [.. timeline.Where(e => e.Kind == Kind.Arrived)];

    /// <summary>Whether any held request ended before <see cref="Release"/>.</summary>
    public bool AnyEndedEarly => timeline.Any(e => e.Kind == Kind.EndedEarly);

    public static async Task<HoldingUpstream> StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        HoldingUpstream upstream = new();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A beat is one byte, far under Kestrel's minimum response rate; the client is the proxy,
            // which reads every byte, but the rate is not this double's subject.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Use(upstream.Count));
        });

        WebApplication app = builder.Build();
        app.Run(context =>
        {
            Task handler = upstream.HoldAsync(context);
            upstream.handlers.Enqueue(handler);
            return handler;
        });
        upstream.app = app;

        await app.StartAsync(ct);
        upstream.BaseAddress = Loopback.AddressOf(app, "holding upstream");

        return upstream;
    }

    /// <summary>
    /// Waits until every one of <paramref name="paths"/> is held and that many connections are open,
    /// polling, under the caller's bound. A bound that runs out is an
    /// <see cref="OperationCanceledException"/>, not a wait that goes on; a held request that ends
    /// first fails at once, with the timeline (#738 Q11).
    /// </summary>
    public async Task WaitForHeldAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        while (OpenConnections < paths.Count || !paths.All(active.ContainsKey))
        {
            if (AnyEndedEarly)
            {
                Assert.Fail("A held request ended before the bound was reached.\n" + Describe());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <summary>Completes when every request this upstream accepted has finished its handler.</summary>
    public Task HandlersFinished() => Task.WhenAll(handlers);

    /// <summary>Ends every held response, and every one that arrives from now on, with its body whole.</summary>
    public void Release() => released.TrySetResult();

    /// <summary>The timeline, one event to a line, for a failure message.</summary>
    public string Describe() => string.Join(
        '\n',
        timeline.Select(e => string.Create(
            CultureInfo.InvariantCulture,
            $"{e.At.TotalMilliseconds,8:F0} ms {e.Kind,-10} {e.ConnectionId} {e.Path} {e.Protocol}")));

    public async ValueTask DisposeAsync()
    {
        Release();
        if (app is not null)
        {
            using CancellationTokenSource stop = new(StopBudget);
            await app.StopAsync(stop.Token);
            await app.DisposeAsync();
        }
    }

    private ConnectionDelegate Count(ConnectionDelegate next) => async connection =>
    {
        int now = Interlocked.Increment(ref open);
        int before;
        do
        {
            before = Volatile.Read(ref peak);
        }
        while (now > before && Interlocked.CompareExchange(ref peak, now, before) != before);

        Record(Kind.Opened, connection.ConnectionId);
        try
        {
            await next(connection);
        }
        finally
        {
            _ = Interlocked.Decrement(ref open);
            Record(Kind.Closed, connection.ConnectionId);
        }
    };

    private async Task HoldAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string connectionId = context.Connection.Id;
        CancellationToken aborted = context.RequestAborted;

        active[path] = connectionId;
        Record(Kind.Arrived, connectionId, path, context.Request.Protocol);
        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain";
            context.Response.Headers[MarkerHeader] = "1";
            await context.Response.StartAsync(aborted);
            await context.Response.Body.FlushAsync(aborted);

            while (!released.Task.IsCompleted)
            {
                await context.Response.WriteAsync(BeatByte, aborted);
                await context.Response.Body.FlushAsync(aborted);
                _ = await Task.WhenAny(released.Task, Task.Delay(Beat, aborted));
                aborted.ThrowIfCancellationRequested();
            }

            await context.Response.WriteAsync(EndOfBody, aborted);
            Record(Kind.Ended, connectionId, path);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // An abort, or a write to a connection the proxy closed. Before Release it is an early
            // end, which a test fails on while it still expects the request held (#738 Q11); after
            // Release it is Ended, and the proof tells a cut body from a whole one by reading it.
            Record(released.Task.IsCompleted ? Kind.Ended : Kind.EndedEarly, connectionId, path);
        }
        finally
        {
            _ = active.TryRemove(path, out _);
        }
    }

    private void Record(Kind kind, string connectionId, string? path = null, string? protocol = null) =>
        timeline.Enqueue(new Event(clock.Elapsed, kind, connectionId, path, protocol));
}
