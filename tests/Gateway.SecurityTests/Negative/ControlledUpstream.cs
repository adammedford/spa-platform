using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.SecurityTests.Negative;

/// <summary>What a <see cref="ControlledUpstream"/> does with a call (N-33 to N-35).</summary>
internal enum UpstreamBehaviour
{
    /// <summary>
    /// Refuses every connection: it names <see cref="ControlledUpstream.RefusedPort"/> on loopback, where
    /// nothing listens, and starts no listener (N-33).
    /// </summary>
    Refuse,

    /// <summary>Accepts the connection, reads the request, and sends nothing at all (N-34).</summary>
    Silent,

    /// <summary>
    /// Answers 200 with <see cref="ControlledUpstream.MarkerHeader"/> at once, then one flushed body
    /// byte every <see cref="ControlledUpstream.Beat"/> until <see cref="ControlledUpstream.Release"/>,
    /// when it ends with <see cref="ControlledUpstream.EndOfBody"/> (N-35).
    /// </summary>
    Trickle,
}

/// <summary>
/// The negative suite's controlled upstream for the three composed failure rows (#741; #715 Q7, Q9):
/// one that refuses, one that stays silent before its headers, and one that holds every call open by
/// trickling its body. Each is listed by the test that uses it, through
/// <see cref="Gateway.TestHarness.GatewayHarness.UpdateRegistryAsync"/>, so <c>fixtures/</c> is not
/// changed.
///
/// <para><b>Why trickle rather than stay silent for the bound.</b> The entry's <c>timeoutSeconds</c> is an
/// idle limit the proxy restarts on every body read (<c>contracts/upstream-contract.md</c> -> Timeouts
/// and failures; T-C17), so a held call that fell silent would time out and free its connection, and
/// the bound would never be reached. Workstream 4's <c>HoldingUpstream</c> holds its calls the same
/// way; this is this owner's copy, because a test project references no other test project.</para>
///
/// <para><b>What it records, and what it never does.</b> Connections are counted in Kestrel's
/// connection middleware, below HTTP, so a connection the proxy's pool opens is counted whether or not
/// a call arrives on it. Every open, close, arrival and end is kept with its connection id, path and
/// protocol, for <see cref="Describe"/> to print in a failure message. No header is recorded: the
/// forwarded call carries the session's access token, and nothing here may put it in a message
/// (N-16).</para>
/// </summary>
internal sealed class ControlledUpstream : IAsyncDisposable
{
    /// <summary>A header on every response this upstream sends, so a response that carries it came from here.</summary>
    public const string MarkerHeader = "X-Controlled-Upstream";

    /// <summary>The loopback port <see cref="UpstreamBehaviour.Refuse"/> names: privileged, and listened on by nothing here.</summary>
    public const int RefusedPort = 1;

    /// <summary>The body byte each beat writes.</summary>
    public const string BeatByte = ".";

    /// <summary>What a released response's body ends with, so a whole body can be told from a cut one.</summary>
    public const string EndOfBody = "released";

    /// <summary>How often a held call is sent a body byte: eight beats in a two-second idle limit.</summary>
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

    private ControlledUpstream(UpstreamBehaviour behaviour) => Behaviour = behaviour;

    /// <summary>What happened on this upstream.</summary>
    public enum Kind
    {
        /// <summary>A connection was accepted.</summary>
        Opened,

        /// <summary>A connection closed.</summary>
        Closed,

        /// <summary>A call arrived.</summary>
        Arrived,

        /// <summary>A held call ended after <see cref="Release"/>, its body whole.</summary>
        Ended,

        /// <summary>A held call ended before <see cref="Release"/>: aborted, or its write failed.</summary>
        EndedEarly,
    }

    /// <summary>One entry of the timeline: a monotonic time since start, the connection, and for a call its path and protocol.</summary>
    public sealed record Event(TimeSpan At, Kind Kind, string ConnectionId, string? Path = null, string? Protocol = null);

    public UpstreamBehaviour Behaviour { get; }

    /// <summary>Where it listens, scheme and authority only, as a registry <c>upstream</c> names it.</summary>
    public string Origin { get; private set; } = "http://127.0.0.1:1";

    /// <summary>The connections open to this upstream now.</summary>
    public int OpenConnections => Volatile.Read(ref open);

    /// <summary>The most connections that were ever open to this upstream at once.</summary>
    public int PeakConnections => Volatile.Read(ref peak);

    /// <summary>Each call held open now, by its path, with the connection it is held on.</summary>
    public IReadOnlyDictionary<string, string> Held => new Dictionary<string, string>(active, StringComparer.Ordinal);

    /// <summary>Every call that reached this upstream, in the order it arrived.</summary>
    public IReadOnlyList<Event> Requests => [.. timeline.Where(e => e.Kind == Kind.Arrived)];

    /// <summary>Whether any held call ended before <see cref="Release"/>.</summary>
    public bool AnyEndedEarly => timeline.Any(e => e.Kind == Kind.EndedEarly);

    public static async Task<ControlledUpstream> StartAsync(UpstreamBehaviour behaviour, CancellationToken ct)
    {
        ControlledUpstream upstream = new(behaviour);
        if (behaviour == UpstreamBehaviour.Refuse)
        {
            // Loopback port 1, as workstream 4's proof uses: a privileged port nothing here listens on and
            // no unprivileged test can bind, so a connect is refused at once. Two alternatives were
            // tried or weighed and fail: a socket bound and never listening is not refused on macOS (the
            // connect times out, observed 2026-11-21), and a listener started and stopped frees an
            // ephemeral port another test may take. The guard test connects to it directly.
            upstream.Origin = $"http://127.0.0.1:{RefusedPort}";
            return upstream;
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A beat is one byte, far under Kestrel's minimum response rate; the rate is not this
            // double's subject.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Use(upstream.Count));
        });

        WebApplication web = builder.Build();
        web.Run(context =>
        {
            Task handler = upstream.AnswerAsync(context);
            upstream.handlers.Enqueue(handler);
            return handler;
        });
        upstream.app = web;

        await web.StartAsync(ct);
        string address = web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the controlled upstream started without an address.");
        upstream.Origin = new Uri(address).GetLeftPart(UriPartial.Authority);

        return upstream;
    }

    /// <summary>
    /// Waits until every one of <paramref name="paths"/> is held and that many connections are open,
    /// polling, under the caller's token. A held call that ends first fails at once, with the timeline.
    /// </summary>
    public async Task WaitForHeldAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        while (OpenConnections < paths.Count || !paths.All(active.ContainsKey))
        {
            Assert.False(AnyEndedEarly, "A held call ended before the bound was reached.\n" + Describe());
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <summary>Waits until at least one call has arrived, under the caller's token.</summary>
    public async Task WaitForArrivalAsync(CancellationToken ct)
    {
        while (Requests.Count == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <summary>Completes when every call this upstream accepted has finished its handler.</summary>
    public Task HandlersFinished() => Task.WhenAll(handlers);

    /// <summary>Ends every held response, and every one that arrives from now on, with its body whole.</summary>
    public void Release() => released.TrySetResult();

    /// <summary>The timeline, one event to a line, for a failure message. Paths and connection ids only.</summary>
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

    private async Task AnswerAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string connectionId = context.Connection.Id;
        CancellationToken aborted = context.RequestAborted;
        Record(Kind.Arrived, connectionId, path, context.Request.Protocol);

        if (Behaviour == UpstreamBehaviour.Silent)
        {
            // Nothing at all, until the proxy gives up on the call or the host stops.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, aborted);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        active[path] = connectionId;
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
            // An abort, or a write to a connection the proxy closed: an early end, which a test fails
            // on while it still expects the call held.
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
