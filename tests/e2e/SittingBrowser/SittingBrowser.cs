using System.Globalization;
using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// The sitting browser's process (GLOSSARY.md -> "Sitting browser"; placed here by #1023): one stack,
/// one profile on it, and commands read a line at a time, each answered with observation lines on
/// <c>lines</c>. It opens the profile and prints a ready line, holds until its stop (the end of its
/// commands, a <c>stop</c> command, or <c>stop</c> cancelled), then closes the profile and the stack,
/// whose browser goes with it, and prints a stop line.
///
/// <para>Of the command surface #1024 owns it carries only what #1032's proof needs: the profile's
/// open (at the start, the ready line), <c>sign-in [app]</c> as the stack's default user, <c>marker</c>
/// and <c>stop</c>; anything else is refused without being echoed. Each line's text is
/// provisional until that surface lands. A line is a UTC time, a token, the profile and named fields;
/// the marker's text is the only free text, and no line carries a URL, a cookie or a password
/// (AGENTS.md's canary rule; N-16).</para>
///
/// <para>It owns the stack it is given and disposes it. It does not stop the Playwright driver, which
/// is the whole process's and shared by every test running in parallel beside the in-process proof:
/// the entry point does that when the process ends (<see cref="Chromium.StopDriverAsync"/>).</para>
/// </summary>
public sealed class SittingBrowser(IBrowserStack stack, TextWriter lines, CancellationToken stop) : IAsyncDisposable
{
    /// <summary>The one profile's name on every line, until #1024's surface names several.</summary>
    public const string ProfileName = "default";

    /// <summary>The app a command acts on when it names none.</summary>
    public const string DefaultApp = "app1";

    /// <summary>What <see cref="RunAsync"/> returns, and the stop line's <c>reason</c>, for each stop.</summary>
    public const string EndOfCommands = "eof";

    /// <inheritdoc cref="EndOfCommands"/>
    public const string StopCommand = "stop";

    /// <inheritdoc cref="EndOfCommands"/>
    public const string Cancelled = "cancelled";

    private bool closed;

    /// <summary>The profile it opened, once the ready line is out; a test reads its cookies and its browser through it.</summary>
    public Profile? Profile { get; private set; }

    /// <summary>
    /// Opens the profile, prints the ready line, answers <paramref name="commands"/> until the stop,
    /// closes everything, prints the stop line, and returns the stop's reason.
    /// </summary>
    public async Task<string> RunAsync(TextReader commands)
    {
        string reason;
        try
        {
            Profile = await stack.OpenCancellableAsync(stop);
            await WriteAsync("ready", $"stack={stack.Kind.ToString().ToLowerInvariant()}");
            reason = await AnswerAsync(commands);
        }
        finally
        {
            await CloseAsync();
        }

        await WriteAsync("stopped", $"reason={reason}");
        return reason;
    }

    private async Task<string> AnswerAsync(TextReader commands)
    {
        while (true)
        {
            // On the thread pool: the console's reader answers ReadLineAsync by reading synchronously
            // (Console.In is a synchronized reader), so called here it would hold this loop, and the stop
            // with it, until the next line came.
            Task<string?> read = Task.Run(() => commands.ReadLineAsync(stop).AsTask(), CancellationToken.None);
            if (!await FinishesBeforeStopAsync(read) || stop.IsCancellationRequested)
            {
                return Cancelled;
            }

            if (await read is not { } line)
            {
                return EndOfCommands;
            }

            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words is [StopCommand])
            {
                return StopCommand;
            }

            if (words.Length > 0)
            {
                Task answered = AnswerAsync(words);
                if (!await FinishesBeforeStopAsync(answered) || stop.IsCancellationRequested)
                {
                    return Cancelled;
                }

                await answered;
            }
        }
    }

    /// <summary>
    /// One command. A Playwright failure, or an app the stack does not have, is a <c>failed</c> line naming
    /// the command and the failure's type, never its message, which may hold a URL; the process goes on.
    /// </summary>
    private async Task AnswerAsync(string[] words)
    {
        Profile profile = Profile!;
        try
        {
            switch (words)
            {
                case ["sign-in", .. string[] rest] when rest.Length <= 1:
                {
                    HarnessApp app = stack.App(rest is [string named] ? named : DefaultApp);
                    string state = await SignInAsync(profile, app);
                    await WriteAsync("state", $"app={app.AppId} state={state.Replace(' ', '-')}");
                    break;
                }

                case ["marker"]:
                {
                    string text = await Redacted.PlaywrightAsync(() => profile.Page.Locator("#marker").InnerTextAsync());
                    await WriteAsync("marker", $"text={JsonSerializer.Serialize(text)}");
                    break;
                }

                default:
                    // The command is not echoed: a line typed by mistake may hold anything.
                    await WriteAsync("refused", "reason=unknown-command");
                    break;
            }
        }
        catch (Exception failed) when (failed is PlaywrightException or TimeoutException or KeyNotFoundException or InvalidOperationException)
        {
            await WriteAsync("failed", $"command={words[0]} error={failed.GetType().Name}");
        }
    }

    /// <summary>
    /// <see cref="Profile.SignInAsync"/>'s steps under this process's token: it calls the stack's
    /// login without one, and nothing the instrument calls reads xUnit's <c>Assert</c> (#1023 Q8).
    /// Returns what the shell's session state says.
    /// </summary>
    private async Task<string> SignInAsync(Profile profile, HarnessApp app)
    {
        await profile.GotoAsync(app.Url("/bff/login").ToString());
        await stack.CompleteLoginCancellableAsync(profile, app, stop);
        return await Redacted.PlaywrightAsync(async () =>
        {
            await profile.Page.WaitForURLAsync(app.Url("/").ToString());
            ILocator state = profile.Page.Locator("#session-state");
            await Assertions.Expect(state).ToHaveTextAsync(Stack.Profile.SignedInState);
            return await state.InnerTextAsync();
        });
    }

    /// <summary>
    /// Whether <paramref name="work"/> ends before the stop; work cancelled by the stop itself may win the
    /// race, so a caller reads the token as well. A read of standard input does not end on
    /// cancellation (the console's reader blocks), and a Playwright call takes no token, so the stop
    /// does not wait for either: what is left of it fails once the profile closes, and is observed here.
    /// </summary>
    private async Task<bool> FinishesBeforeStopAsync(Task work)
    {
        Task stopped = Task.Delay(Timeout.Infinite, stop);
        if (await Task.WhenAny(work, stopped) == work)
        {
            return true;
        }

        _ = work.ContinueWith(static abandoned => _ = abandoned.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return false;
    }

    private async Task WriteAsync(string token, string fields)
    {
        string at = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        await lines.WriteLineAsync($"{at} {token} profile={ProfileName} {fields}");

        // A parent reading a pipe sees a line only once it is flushed, and the ready line is what it waits
        // for. Console.Out flushes itself; a writer given here that buffers does not.
        await lines.FlushAsync();
    }

    /// <summary>The profile's context, then the stack: its browser, and on the local stack the cell. Once.</summary>
    private async Task CloseAsync()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        try
        {
            if (Profile is { } profile)
            {
                await profile.DisposeAsync();
            }
        }
        finally
        {
            await stack.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
