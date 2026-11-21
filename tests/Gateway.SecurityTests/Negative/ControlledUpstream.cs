using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gateway.SecurityTests.Negative;

/// <summary>What a <see cref="ControlledUpstream"/> does with a call (N-33 to N-35).</summary>
internal enum UpstreamBehaviour
{
    /// <summary>
    /// Refuses every connection: it names <see cref="ControlledUpstream.RefusedPort"/> on loopback, where
    /// nothing listens, and starts no listener (N-33).
    /// </summary>
    Refuse,

    /// <summary>Accepts the connection, reads the request head, and sends nothing at all (N-34).</summary>
    Silent,

    /// <summary>
    /// Answers 200 with <see cref="ControlledUpstream.MarkerHeader"/> and a chunked body at once, then
    /// one flushed one-byte chunk every <see cref="ControlledUpstream.Beat"/> until
    /// <see cref="ControlledUpstream.Release"/>, when it writes <see cref="ControlledUpstream.EndOfBody"/>,
    /// the terminating chunk, and closes (N-35).
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
/// <para><b>Why a bare socket.</b> Adam's Confirm of 2026-11-04 on #799 (Q13, #741's Q2) chose a
/// suite-local concurrent bare-socket double: each accepted socket is one connection, counted where it
/// is accepted and closed, and the bytes written are the bytes on the wire, as
/// <see cref="HostileUpstream"/> writes its own. It speaks only HTTP/1.1, the protocol the bound is
/// scoped to (#733, #738). Unlike <see cref="HostileUpstream"/> it serves connections concurrently,
/// because N-35 holds the bound's number of them open at once.</para>
///
/// <para><b>Why trickle rather than stay silent for the bound.</b> The entry's <c>timeoutSeconds</c> is an
/// idle limit the proxy restarts on every body read (<c>contracts/upstream-contract.md</c> -> Timeouts
/// and failures; T-C17), so a held call that fell silent would time out and free its connection, and
/// the bound would never be reached. Each chunk is flushed, because a scheduled write is not activity
/// the proxy saw (Confirm Q6, Q16).</para>
///
/// <para><b>What it records, and what it never does.</b> Every accept, close, arrival and end is kept
/// with its connection id, path and protocol, for <see cref="Describe"/> to print in a failure
/// message, with the body bytes it wrote to each path. No header is kept: the forwarded call carries
/// the session's access token, and nothing here may put it in a message (N-16). The direct control
/// test plants canary header values and asserts the record lacks them.</para>
///
/// <para>Every operation is bounded: reading a request head by <see cref="HeadBudget"/>, each write by
/// <see cref="WriteBudget"/>, and disposal by <see cref="StopBudget"/>. A handler that fails other
/// than by its connection closing faults its task, and <see cref="HandlersFinished"/> surfaces it.</para>
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

    /// <summary>How long a connection may take to send its request head.</summary>
    private static readonly TimeSpan HeadBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long one write to the proxy may take.</summary>
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long disposal waits for the accept loop and the handlers, so a stuck one cannot hold a test's clean-up.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stopping = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Event> timeline = new();
    private readonly ConcurrentDictionary<string, string> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> written = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Task> handlers = new();
    private TcpListener? listener;
    private Task accepting = Task.CompletedTask;
    private int connections;
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

        /// <summary>A held call ended before <see cref="Release"/>: its peer closed, or a write failed.</summary>
        EndedEarly,
    }

    /// <summary>One entry of the timeline: a monotonic time since start, the connection, and for a call its path and protocol.</summary>
    public sealed record Event(TimeSpan At, Kind Kind, string ConnectionId, string? Path = null, string? Protocol = null);

    public UpstreamBehaviour Behaviour { get; }

    /// <summary>Where it listens, scheme and authority only, as a registry <c>upstream</c> names it.</summary>
    public string Origin { get; private set; } = $"http://127.0.0.1:{RefusedPort}";

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

    /// <summary>Whether any connection to this upstream has closed, read from the retained timeline.</summary>
    public bool AnyClosed => timeline.Any(e => e.Kind == Kind.Closed);

    public static Task<ControlledUpstream> StartAsync(UpstreamBehaviour behaviour, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ControlledUpstream upstream = new(behaviour);
        if (behaviour == UpstreamBehaviour.Refuse)
        {
            // Loopback port 1, as workstream 4's proof uses: a privileged port nothing here listens on and
            // no unprivileged test can bind, so a connect is refused at once. Two alternatives were
            // tried or weighed and fail: a socket bound and never listening is not refused on macOS (the
            // connect times out, observed 2026-11-21), and a listener started and stopped frees an
            // ephemeral port another test may take. N-33 checks the refusal itself before it asserts
            // the gateway's answer (Confirm Q14).
            return Task.FromResult(upstream);
        }

        TcpListener listening = new(IPAddress.Loopback, 0);
        listening.Start();
        upstream.listener = listening;
        upstream.Origin = $"http://127.0.0.1:{((IPEndPoint)listening.LocalEndpoint).Port}";
        upstream.accepting = upstream.AcceptAsync(listening);
        return Task.FromResult(upstream);
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

    /// <summary>Completes when a call for <paramref name="path"/> has arrived, polling, under the caller's token.</summary>
    public async Task WaitForPathAsync(string path, CancellationToken ct)
    {
        while (!Requests.Any(seen => seen.Path == path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <summary>The body this upstream wrote to the call for <paramref name="path"/>, once that call has ended; null before.</summary>
    public string? WrittenBody(string path) => written.TryGetValue(path, out string? body) ? body : null;

    /// <summary>Completes when every connection this upstream accepted has finished its handler, and faults if one failed.</summary>
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
        await stopping.CancelAsync();
        listener?.Stop();

        // Clean-up throws nothing, so a test's own failure is the one reported; a handler's fault is
        // surfaced by the test that awaits HandlersFinished, not here.
        using CancellationTokenSource budget = new(StopBudget);
        try
        {
            await Task.WhenAll(accepting, HandlersFinished()).WaitAsync(budget.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }

        stopping.Dispose();
    }

    private async Task AcceptAsync(TcpListener listening)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listening.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            string id = "c" + Interlocked.Increment(ref connections).ToString(CultureInfo.InvariantCulture);
            int now = Interlocked.Increment(ref open);
            int before;
            do
            {
                before = Volatile.Read(ref peak);
            }
            while (now > before && Interlocked.CompareExchange(ref peak, now, before) != before);

            Record(Kind.Opened, id);
            handlers.Enqueue(Task.Run(() => ServeAsync(client, id)));
        }
    }

    private async Task ServeAsync(TcpClient client, string id)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                (string path, string protocol)? call = await ReadHeadAsync(stream);
                if (call is not { } arrived)
                {
                    return;
                }

                Record(Kind.Arrived, id, arrived.path, arrived.protocol);
                if (Behaviour == UpstreamBehaviour.Silent)
                {
                    // Nothing at all, until the proxy gives up on the call and closes, or the double stops.
                    await PeerClosedAsync(stream);
                    return;
                }

                await TrickleAsync(stream, id, arrived.path);
            }
        }
        finally
        {
            _ = Interlocked.Decrement(ref open);
            Record(Kind.Closed, id);
        }
    }

    /// <summary>The request line's path and protocol, or null when the connection closed first. No header is kept.</summary>
    private async Task<(string Path, string Protocol)?> ReadHeadAsync(NetworkStream stream)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        budget.CancelAfter(HeadBudget);
        byte[] buffer = new byte[16384];
        int read = 0;
        int end;
        while ((end = buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (read == buffer.Length)
            {
                throw new InvalidOperationException("the controlled upstream was sent a request head over 16 KB.");
            }

            int got;
            try
            {
                got = await stream.ReadAsync(buffer.AsMemory(read), budget.Token);
            }
            catch (IOException)
            {
                // The peer reset the connection before its head was whole: no call arrived.
                return null;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return null;
            }

            if (got == 0)
            {
                return null;
            }

            read += got;
        }

        // The head ends with an empty line, so its first line break is always found.
        int eol = buffer.AsSpan(0, end + 2).IndexOf("\r\n"u8);
        string line = Encoding.ASCII.GetString(buffer, 0, eol);
        string[] parts = line.Split(' ');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("the controlled upstream was sent a request line it cannot read.");
        }

        return (parts[1], parts[2]);
    }

    /// <summary>Completes when the peer closes the connection or the double stops; the proxy sends nothing more on a connection awaiting its answer.</summary>
    private async Task PeerClosedAsync(NetworkStream stream)
    {
        byte[] probe = new byte[1];
        try
        {
            while (await stream.ReadAsync(probe, stopping.Token) > 0)
            {
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task TrickleAsync(NetworkStream stream, string id, string path)
    {
        active[path] = id;
        StringBuilder body = new();
        try
        {
            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                $"{MarkerHeader}: 1\r\n" +
                "Connection: close\r\n\r\n");

            Task peerClosed = PeerClosedAsync(stream);
            while (!released.Task.IsCompleted)
            {
                await WriteAsync(stream, Chunk(BeatByte));
                _ = body.Append(BeatByte[0]);
                _ = await Task.WhenAny(released.Task, peerClosed, Task.Delay(Beat, stopping.Token));
                if (peerClosed.IsCompleted && !released.Task.IsCompleted)
                {
                    throw new IOException("the proxy closed a held connection.");
                }
            }

            await WriteAsync(stream, Chunk(EndOfBody) + "0\r\n\r\n");
            _ = body.Append(EndOfBody);
            written[path] = body.ToString();
            Record(Kind.Ended, id, path);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // A peer that closed, a write to a connection the proxy closed, or the double stopping: an
            // early end, which a test fails on while it still expects the call held.
            Record(released.Task.IsCompleted ? Kind.Ended : Kind.EndedEarly, id, path);
        }
        finally
        {
            _ = active.TryRemove(path, out _);
        }
    }

    private async Task WriteAsync(NetworkStream stream, string text)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        budget.CancelAfter(WriteBudget);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), budget.Token);
        await stream.FlushAsync(budget.Token);
    }

    private static string Chunk(string data) =>
        string.Create(CultureInfo.InvariantCulture, $"{data.Length:x}\r\n{data}\r\n");

    private void Record(Kind kind, string connectionId, string? path = null, string? protocol = null) =>
        timeline.Enqueue(new Event(clock.Elapsed, kind, connectionId, path, protocol));
}
