using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// The sitting browser's resident process (#1024 Q2, #1033): one per sitting, started in the
/// background with <c>start</c>, listening on a Unix socket at <c>browser.sock</c> in the sitting's
/// bundle directory (mode 700, never uploaded), and keeping its stack and every profile until
/// <c>stop</c> or a signal. Every other command is a <see cref="SittingBrowserClient"/> call: one
/// request line, a JSON array of the command's words, answered with observation lines and an exit
/// line the client does not print. The password never crosses the socket: no command takes one.
///
/// <para>It refuses to start when <c>PWDEBUG</c> is set or <c>DEBUG</c> names a <c>pw:</c> channel,
/// Playwright's opt-in surfaces that print what a page holds (#1024 Q6), and when another process
/// already answers on the socket. A socket file nothing answers on is a crashed process's, and is
/// removed.</para>
/// </summary>
public static class SittingBrowserServer
{
    /// <summary>The socket, in the bundle directory. A Unix socket's path is at most 103 bytes on macOS, 107 on Linux.</summary>
    public const string SocketName = "browser.sock";

    /// <summary>The process's own output (Playwright, the runner, the local cell's hosts): this file, mode 600, and never the terminal.</summary>
    public const string ServerLog = "browser.server.log";

    /// <summary>The answer's last line, which carries the exit code and is not printed.</summary>
    public const string ExitPrefix = "#exit ";

    /// <summary>The stop line's reason after <c>stop</c>.</summary>
    public const string StopReason = "stop";

    /// <summary>The stop line's reason after a signal.</summary>
    public const string CancelledReason = "cancelled";

    /// <summary>Why the environment refuses a start, or null: <c>pwdebug</c> or <c>debug-pw</c>.</summary>
    public static string? RefusalOf(IReadOnlyDictionary<string, string?> environment)
    {
        if (environment.ContainsKey("PWDEBUG"))
        {
            return "pwdebug";
        }

        return environment.GetValueOrDefault("DEBUG") is { } debug && debug.Contains("pw:", StringComparison.Ordinal) ? "debug-pw" : null;
    }

    /// <summary>The process's environment, as <see cref="RefusalOf"/> reads it.</summary>
    public static IReadOnlyDictionary<string, string?> CurrentEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);

    /// <summary>
    /// Runs the process: refuses, or starts the stack, prints its ready line on <paramref name="lines"/>,
    /// answers every connection until <c>stop</c> or <paramref name="cancel"/>, closes every profile and
    /// the stack, removes the socket and prints its stopped line. Returns its exit code: 0 after a
    /// stop, 1 on a refusal or a stack that did not start.
    /// </summary>
    public static async Task<int> RunAsync(string bundle, TextWriter lines, Func<CancellationToken, Task<IBrowserStack>> startStack,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancel, Action<SittingBrowser>? started = null)
    {
        if (RefusalOf(environment) is { } refusal)
        {
            await WriteAsync(lines, Observations.Refused(DateTimeOffset.UtcNow, refusal));
            return 1;
        }

        string socketPath = Path.Combine(Path.GetFullPath(bundle), SocketName);
        if (File.Exists(socketPath))
        {
            if (await SittingBrowserClient.AnswersAsync(socketPath, cancel))
            {
                await WriteAsync(lines, Observations.Refused(DateTimeOffset.UtcNow, "running"));
                return 1;
            }

            File.Delete(socketPath);
        }

        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException taken) when (taken.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // Another start bound it between the check and here.
            await WriteAsync(lines, Observations.Refused(DateTimeOffset.UtcNow, "running"));
            return 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            await WriteAsync(lines, Observations.Refused(DateTimeOffset.UtcNow, "socket-path-too-long"));
            return 1;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            listener.Listen(16);
            IBrowserStack stack;
            try
            {
                stack = await startStack(cancel);
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                await Console.Error.WriteLineAsync($"sitting-browser: the stack did not start: {failed.GetType().Name}: {Redacted.Text(failed.Message, [])}");
                await WriteAsync(lines, Observations.Failed(DateTimeOffset.UtcNow, ObservationLine.NoProfile, SittingCommand.Start, failed.GetType().Name));
                return 1;
            }

            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            string reason = CancelledReason;
            List<Task> answering = [];
            await using (SittingBrowser sitting = new(stack, bundle, stopping.Token))
            {
                started?.Invoke(sitting);
                await WriteAsync(lines, Observations.Ready(DateTimeOffset.UtcNow, stack.Kind.ToString().ToLowerInvariant()));
                try
                {
                    while (true)
                    {
                        Socket connection = await listener.AcceptAsync(stopping.Token);
                        answering.Add(AnswerAsync(connection, sitting, () =>
                        {
                            reason = StopReason;
                            stopping.Cancel();
                        }, stopping.Token));
                    }
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // A stop or a signal: the profiles close as the browser does.
                }
            }

            // What was mid-answer when the stack closed fails and ends; none of it outlasts the stop for long.
            await Task.WhenAll(answering).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(static _ => { }, TaskScheduler.Default);
            await WriteAsync(lines, Observations.Stopped(DateTimeOffset.UtcNow, reason));
            return 0;
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>One connection: its request line, the answer's lines, the exit line. A <c>stop</c> closes every profile before it answers.</summary>
    private static async Task AnswerAsync(Socket connection, SittingBrowser sitting, Action stopRequested, CancellationToken stopping)
    {
        using Socket owned = connection;
        await using NetworkStream stream = new(owned, ownsSocket: false);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        async Task Write(string line)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync(CancellationToken.None);
        }

        try
        {
            string? request = await reader.ReadLineAsync(stopping);
            string[]? words = request is null ? null : JsonSerializer.Deserialize<string[]>(request);
            (SittingCommand? command, string? _) = words is null ? (null, null) : SittingCommand.Parse(words);
            int exit;
            if (command is null)
            {
                exit = 2;
            }
            else if (command.Verb == SittingCommand.Stop)
            {
                await sitting.StopAsync(Write);
                exit = 0;
                stopRequested();
            }
            else if (command.Verb == SittingCommand.Start)
            {
                await Write(Observations.Refused(DateTimeOffset.UtcNow, "running"));
                exit = 1;
            }
            else
            {
                exit = await sitting.AnswerAsync(command, Write);
            }

            await Write($"{ExitPrefix}{exit}");
        }
        catch (Exception gone) when (gone is IOException or SocketException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            // The client went away, or the process stopped under it: nothing is left to answer.
        }
    }

    private static async Task WriteAsync(TextWriter lines, string line)
    {
        await lines.WriteLineAsync(line);
        await lines.FlushAsync();
    }
}

/// <summary>
/// A short client call to the resident process (#1024 Q2): connects to the socket, writes the
/// command's words as one request line, prints the answer's lines, and returns the exit line's code.
/// With nothing answering at the socket it prints <c>not-running</c> and returns 1.
/// </summary>
public static class SittingBrowserClient
{
    public static async Task<int> SendAsync(string bundle, SittingCommand command, IReadOnlyList<string> words, TextWriter output, CancellationToken ct)
    {
        string socketPath = Path.Combine(Path.GetFullPath(bundle), SittingBrowserServer.SocketName);
        Socket? socket = await ConnectAsync(socketPath, ct);
        if (socket is null)
        {
            await output.WriteLineAsync(Observations.NotRunning(DateTimeOffset.UtcNow, command.Profile));
            await output.FlushAsync(ct);
            return 1;
        }

        using Socket owned = socket;
        await using NetworkStream stream = new(owned, ownsSocket: false);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        using StreamReader reader = new(stream, Encoding.UTF8);
        await writer.WriteLineAsync(JsonSerializer.Serialize(words));
        await writer.FlushAsync(ct);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith(SittingBrowserServer.ExitPrefix, StringComparison.Ordinal))
                {
                    return int.TryParse(line.AsSpan(SittingBrowserServer.ExitPrefix.Length), System.Globalization.CultureInfo.InvariantCulture, out int exit) ? exit : 1;
                }

                await output.WriteLineAsync(line);
                await output.FlushAsync(ct);
            }
        }
        catch (IOException)
        {
            // The process stopped mid-answer.
        }

        return 1;
    }

    /// <summary>Whether a process answers at the socket.</summary>
    internal static async Task<bool> AnswersAsync(string socketPath, CancellationToken ct)
    {
        using Socket? socket = await ConnectAsync(socketPath, ct);
        return socket is not null;
    }

    private static async Task<Socket?> ConnectAsync(string socketPath, CancellationToken ct)
    {
        if (!File.Exists(socketPath))
        {
            return null;
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }
}
