using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// The sitting browser's entry point and process (#1032), with no cell: the entry point that replaced
/// xUnit's generated one still finds the suite, and the process signs in on the local stack, reads the
/// marker, prints nothing a sitting may not post, and stops on its token and on the end of its input.
/// The password's path (<c>DeployedStack</c>'s login form) is proven in the first sitting on the cell
/// (#1027), since no provider here shows a form.
/// </summary>
public sealed partial class SittingBrowserTests
{
    /// <summary>How long a stop may take: the profile, the browser and the local cell closing.</summary>
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);

    /// <summary>How long the built executable may take to print its ready line: the browser installer's check, the cell, Chromium.</summary>
    private static readonly TimeSpan ReadyBound = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task The_executable_lists_with_the_console_runner_every_test_method_the_assembly_declares()
    {
        // -list methods prints each test method once, a theory once whatever its rows, as the
        // reflection below counts them; its JSON is the first line, and a colour reset may follow.
        ChildResult listed = await RunExecutableAsync(["-list", "methods/json", "-noColor"], atReady: null, TimeSpan.FromMinutes(2));
        Assert.Equal(0, listed.ExitCode);
        string json = listed.Output.Split('\n')[0];
        HashSet<string> methods = [.. JsonSerializer.Deserialize<string[]>(json)!];

        HashSet<string> declared = [.. typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any())
                .Select(method => $"{type.FullName}.{method.Name}"))];

        Assert.NotEmpty(declared);
        Assert.Contains($"{typeof(SittingBrowserTests).FullName}.{nameof(The_executable_lists_with_the_console_runner_every_test_method_the_assembly_declares)}", declared);
        Assert.Equal(declared.Order(StringComparer.Ordinal), methods.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task It_signs_in_as_the_default_user_and_reads_the_marker_on_the_switchs_stack()
    {
        Session session = await SignInAndReadMarkerAsync();
        string[] lines = session.Lines;

        Assert.Matches(Line("ready", "stack=local"), lines[0]);
        Assert.Contains(lines, line => Line("state", $"app=app1 state={Profile.SignedInState.Replace(' ', '-')}").IsMatch(line));
        Assert.Contains(lines, line => Line("marker", $"text={Regex.Escape(JsonSerializer.Serialize(FixtureMarker("app1")))}").IsMatch(line));
        Assert.Matches(Line("stopped", $"reason={SittingBrowser.StopCommand}"), lines[^1]);
        Assert.Equal(SittingBrowser.StopCommand, session.Reason);
    }

    [Fact]
    public async Task What_it_prints_holds_no_cookie_value_and_no_url_with_a_query()
    {
        Session session = await SignInAndReadMarkerAsync();

        Assert.NotEmpty(session.Cookies);
        foreach (string value in session.Cookies.Select(cookie => cookie.Value).Where(value => value.Length > 0))
        {
            Assert.DoesNotContain(value, session.Text, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(UrlWithQuery(), session.Text);
    }

    [Fact]
    public async Task A_command_it_cannot_answer_is_one_line_that_echoes_nothing_typed_and_it_goes_on()
    {
        using CancellationTokenSource stop = new();
        CapturedLines lines = new();
        Commands commands = new();
        IBrowserStack stack = await Stacks.StartAsync();
        await using SittingBrowser sitting = new(stack, lines, stop.Token);
        Task<string> run = sitting.RunAsync(commands);
        await lines.WaitForAsync("ready", run);

        commands.Send("sign-in no-such-app");
        await lines.WaitForAsync("failed", run);
        commands.Send("typed-by-mistake s3cr3t-value");
        await lines.WaitForAsync("refused", run);
        commands.Send("stop");
        await run.WaitAsync(StopBound, TestContext.Current.CancellationToken);

        Assert.Contains(lines.Lines, line => Line("failed", "command=sign-in error=KeyNotFoundException").IsMatch(line));
        Assert.Contains(lines.Lines, line => Line("refused", "reason=unknown-command").IsMatch(line));
        Assert.DoesNotContain("no-such-app", lines.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", lines.Text, StringComparison.Ordinal);
        Assert.Matches(Line("stopped", $"reason={SittingBrowser.StopCommand}"), lines.Lines[^1]);
    }

    [Fact]
    public async Task Cancelling_its_token_stops_it_within_a_bound_and_closes_its_browser()
    {
        using CancellationTokenSource stop = new();
        CapturedLines lines = new();
        Commands commands = new();
        IBrowserStack stack = await Stacks.StartAsync();
        await using SittingBrowser sitting = new(stack, lines, stop.Token);
        Task<string> run = sitting.RunAsync(commands);
        await lines.WaitForAsync("ready", run);

        // A command is waiting on its input, which never ends: only the token can stop it.
        await stop.CancelAsync();
        string reason = await run.WaitAsync(StopBound, TestContext.Current.CancellationToken);

        Assert.Equal(SittingBrowser.Cancelled, reason);
        Assert.Matches(Line("stopped", $"reason={SittingBrowser.Cancelled}"), lines.Lines[^1]);
        IBrowser browser = sitting.Profile!.Context.Browser!;
        Assert.False(browser.IsConnected, "the stop should have closed the stack, and its Chromium with it.");
    }

    [Fact]
    public async Task The_executable_with_sitting_browser_prints_its_ready_line_and_exits_0_at_the_end_of_its_input()
    {
        ChildResult child = await RunExecutableAsync([Program.SittingBrowserCommand], atReady: process =>
        {
            process.StandardInput.Close();
            return Task.CompletedTask;
        }, ReadyBound);

        Assert.True(child.ExitCode == 0, $"exit {child.ExitCode}; standard error: {child.Error}");
        string[] lines = [.. child.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        Assert.Matches(Line("ready", "stack=local"), lines[0]);
        Assert.Matches(Line("stopped", $"reason={SittingBrowser.EndOfCommands}"), lines[^1]);

        // Standard output is observation lines alone: the local cell's hosts log to the console too.
        Assert.All(lines, line => Assert.Matches(Line("[a-z-]+", ".*"), line));
    }

    [Fact]
    public async Task The_executable_stops_on_sigterm_through_its_own_token_and_exits_0()
    {
        // kill(1) sends SIGTERM by name on macOS and Linux, the machines the suite runs on; Process.Kill sends SIGKILL.
        ChildResult child = await RunExecutableAsync([Program.SittingBrowserCommand], atReady: async process =>
        {
            using Process kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, kill.ExitCode);
        }, ReadyBound);

        Assert.True(child.ExitCode == 0, $"exit {child.ExitCode}; standard error: {child.Error}");
        string[] lines = [.. child.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        Assert.Matches(Line("stopped", $"reason={SittingBrowser.Cancelled}"), lines[^1]);
    }

    /// <summary>A sign-in, a marker read and a stop on the switch's stack, with the cookies the profile held before the stop.</summary>
    private static async Task<Session> SignInAndReadMarkerAsync()
    {
        using CancellationTokenSource stop = new();
        CapturedLines lines = new();
        Commands commands = new();
        IBrowserStack stack = await Stacks.StartAsync();
        await using SittingBrowser sitting = new(stack, lines, stop.Token);
        Task<string> run = sitting.RunAsync(commands);
        await lines.WaitForAsync("ready", run);

        commands.Send("sign-in");
        await lines.WaitForAsync("state", run);
        commands.Send("marker");
        await lines.WaitForAsync("marker", run);
        IReadOnlyList<BrowserContextCookiesResult> cookies = await sitting.Profile!.CookiesOfAsync(stack.App("app1"));
        commands.Send("stop");
        string reason = await run.WaitAsync(StopBound, TestContext.Current.CancellationToken);
        return new Session(lines.Text, [.. lines.Lines], cookies, reason);
    }

    /// <summary>The fixture shell's <c>#marker</c> heading, read from the fixture (fixtures/spa).</summary>
    private static string FixtureMarker(string appId)
    {
        string html = Encoding.UTF8.GetString(FixtureFiles.Spa(appId)["index.html"]);
        Match marker = MarkerHeading().Match(html);
        Assert.True(marker.Success, $"fixtures/spa/{appId}/index.html has no #marker heading.");
        return marker.Groups[1].Value;
    }

    /// <summary>An observation line: a UTC time to the millisecond, the token, the profile, then <paramref name="fields"/> as a pattern.</summary>
    private static Regex Line(string token, string fields) =>
        new($@"^\d{{4}}-\d{{2}}-\d{{2}}T\d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}}Z {token} profile={SittingBrowser.ProfileName} {fields}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The suite's own executable, as <c>ProcessCommandRunner</c> starts a command, with the stack switch
    /// unset. With <paramref name="atReady"/> set, standard input is held open and the action runs once
    /// a ready line appears; without it, standard input is closed at once.
    /// </summary>
    private static async Task<ChildResult> RunExecutableAsync(string[] arguments, Func<Process, Task>? atReady, TimeSpan bound)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Gateway.E2ETests.exe" : "Gateway.E2ETests");
        Assert.True(File.Exists(executable), $"the suite's executable is not at {executable}.");
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(StackSwitch.Variable);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(bound);
        StringBuilder output = new();
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (atReady is null)
            {
                process.StandardInput.Close();
            }

            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                output.Append(line).Append('\n');
                if (atReady is not null && line.Contains(" ready ", StringComparison.Ordinal))
                {
                    await atReady(process);
                }
            }

            await process.WaitForExitAsync(timeout.Token);
            return new ChildResult(process.ExitCode, output.ToString(), await error);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"{Path.GetFileName(executable)} {string.Join(' ', arguments)} did not finish within {bound}; it printed: {output}");
        }
    }

    [GeneratedRegex("""<h1 id="marker">([^<]+)</h1>""", RegexOptions.None, 1000)]
    private static partial Regex MarkerHeading();

    /// <summary>A scheme, <c>://</c>, then a query somewhere before the next whitespace or quote.</summary>
    [GeneratedRegex("""[A-Za-z][A-Za-z0-9+.-]*://[^\s"']*\?""", RegexOptions.None, 1000)]
    private static partial Regex UrlWithQuery();

    private sealed record Session(string Text, string[] Lines, IReadOnlyList<BrowserContextCookiesResult> Cookies, string Reason);

    private sealed record ChildResult(int ExitCode, string Output, string Error);

    /// <summary>Commands sent one line at a time, whose input never ends: only a stop ends a run over it.</summary>
    private sealed class Commands : TextReader
    {
        private readonly Channel<string> pending = Channel.CreateUnbounded<string>();

        public void Send(string line) => Assert.True(pending.Writer.TryWrite(line));

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await pending.Reader.WaitToReadAsync(cancellationToken) && pending.Reader.TryRead(out string? line) ? line : null;
    }

    /// <summary>What the sitting browser wrote, by line, readable while it runs.</summary>
    private sealed class CapturedLines : TextWriter
    {
        private readonly StringBuilder text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (text)
            {
                text.Append(value);
            }
        }

        public string Text
        {
            get
            {
                lock (text)
                {
                    return text.ToString();
                }
            }
        }

        public string[] Lines => Text.Split(NewLine, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Waits for a line carrying <paramref name="token"/>; fails if the run ends first or none comes within a minute.</summary>
        public async Task WaitForAsync(string token, Task run)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (!Lines.Any(line => line.Contains($" {token} ", StringComparison.Ordinal)))
            {
                if (run.IsCompleted)
                {
                    await run;
                    Assert.Fail($"the sitting browser stopped before a {token} line; it printed: {Text}");
                }

                Assert.True(waited.Elapsed < TimeSpan.FromMinutes(1), $"no {token} line within a minute; it printed: {Text}");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
