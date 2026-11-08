using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// The sitting browser's commands (GLOSSARY.md -> "Sitting browser"; #1024, #1033): one stack, the
/// profiles the agent opens on it by name, and each command answered with observation lines
/// (<see cref="Observations"/>) and an exit code, 0 whenever an observation was made, whatever its
/// token, 1 when none could be, and 2 on usage (<see cref="SittingCommand.Parse"/>). The resident process
/// (<see cref="SittingBrowserServer"/>) hands it each request that reaches its socket; a test hands it
/// commands directly.
///
/// <para>Nothing it prints carries a URL's query or fragment, a hostname that is not an app's (the
/// provider is <c>host=provider</c>, anything else <c>host=elsewhere</c>), a cookie, a session id, a
/// password, a <c>/bff/session</c> or <c>/api</c> body, or the provider's page text (#1024 Q5). A page
/// that ends as <c>other</c> has its query-less URL and visible text written to
/// <c>browser-pages/&lt;UTC time&gt;.txt</c> in the bundle directory, through
/// <see cref="NavigationHold.ShownAsync"/>'s scrub, as #1003 does for a timed-out hold.</para>
///
/// <para>It owns the stack it is given and disposes it. It does not stop the Playwright driver, which
/// is the whole process's: the entry point does that when the process ends.</para>
/// </summary>
public sealed partial class SittingBrowser(IBrowserStack stack, string bundle, CancellationToken stop) : IAsyncDisposable
{
    /// <summary>The file in the bundle directory every <c>watch</c> line is appended to.</summary>
    public const string WatchLog = "browser.log";

    /// <summary>The directory in the bundle directory an <c>other</c> page's text goes to.</summary>
    public const string PagesDirectory = "browser-pages";

    private readonly Lock gate = new();
    private readonly Dictionary<string, OpenProfile> profiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loginTurn = new(1, 1);
    private readonly SemaphoreSlim watchLogTurn = new(1, 1);
    private bool disposed;

    /// <summary>The stack its profiles are on.</summary>
    public IBrowserStack Stack => stack;

    /// <summary>How long a command may take to reach an observation before it is <c>timeout</c>.</summary>
    public TimeSpan Bound { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a page must show the same thing, neither signed in nor a login prompt, to be <c>other</c>.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A profile's Playwright context and page, for a test that reads its cookies; null when none is open by that name.</summary>
    public Profile? ProfileNamed(string name)
    {
        lock (gate)
        {
            return profiles.TryGetValue(name, out OpenProfile? open) ? open.Profile : null;
        }
    }

    /// <summary>Answers one command with lines through <paramref name="write"/>, and returns its exit code.</summary>
    public async Task<int> AnswerAsync(SittingCommand command, Func<string, Task> write)
    {
        try
        {
            return command.Verb switch
            {
                SittingCommand.Status => await StatusAsync(write),
                SittingCommand.Open => await OpenAsync(command, write),
                SittingCommand.Login => await LoginAsync(command, write),
                SittingCommand.Close => await CloseAsync(command, write),
                SittingCommand.Watch => await WatchAsync(command, write),
                SittingCommand.Unwatch => await UnwatchAsync(command, write),
                SittingCommand.Reload or SittingCommand.Marker or SittingCommand.Session or SittingCommand.Api or SittingCommand.ClearProviderVerb
                    => await OnProfileAsync(command, write),
                _ => await FailAsync(write, command, "not-a-profile-command"),
            };
        }
        catch (Exception failed) when (FailureOf(failed) is { } failure)
        {
            return await FailAsync(write, command, failure.Reason, failure.Text);
        }
    }

    /// <summary>What a command's <c>failed</c> line says of a failure it answers, or null for one it does not.</summary>
    private static (string Reason, string? Text)? FailureOf(Exception failed) => failed switch
    {
        // The password's SSM read: its message, the account masked, so an AccessDenied reaches the stream (#1024 Q10).
        CommandFailedException => ("aws", failed.Message),

        // A closed browser among them. The type, never the message, which may hold a URL.
        PlaywrightException or InvalidOperationException or KeyNotFoundException => (failed.GetType().Name, null),
        _ => null,
    };

    /// <summary>Closes every profile, a line each, and ends every watch; the stack goes at <see cref="DisposeAsync"/>.</summary>
    public async Task StopAsync(Func<string, Task> write)
    {
        OpenProfile[] open;
        lock (gate)
        {
            open = [.. profiles.Values];
            profiles.Clear();
        }

        foreach (OpenProfile profile in open)
        {
            await EndWatchAsync(profile);
            await profile.Profile.DisposeAsync();
            await write(Observations.Closed(DateTimeOffset.UtcNow, profile.Name, profile.App.AppId));
        }
    }

    private async Task<int> StatusAsync(Func<string, Task> write)
    {
        OpenProfile[] open;
        lock (gate)
        {
            open = [.. profiles.Values.OrderBy(profile => profile.Name, StringComparer.Ordinal)];
        }

        if (open.Length == 0)
        {
            await write(Observations.NoProfiles(DateTimeOffset.UtcNow));
            return 0;
        }

        foreach (OpenProfile profile in open)
        {
            await write(Observations.Status(DateTimeOffset.UtcNow, profile.Name, profile.App.AppId, profile.Last, profile.LastAt));
        }

        return 0;
    }

    private async Task<int> OpenAsync(SittingCommand command, Func<string, Task> write)
    {
        if (ProfileNamed(command.Profile) is not null)
        {
            return await FailAsync(write, command, "already-open");
        }

        if (AppOf(command.App!) is not { } app)
        {
            return await FailAsync(write, command, "unknown-app");
        }

        OpenProfile open = await AddAsync(command.Profile, app);
        return await OnTurnAsync(open, () => NavigateAsync(open, write, () => open.Profile.Page.GotoAsync(app.Url("/").ToString(), new() { Timeout = Milliseconds(Bound) }), login: false));
    }

    private async Task<int> LoginAsync(SittingCommand command, Func<string, Task> write)
    {
        if (AppOf(command.App!) is not { } app)
        {
            return await FailAsync(write, command, "unknown-app");
        }

        OpenProfile open = ProfileNamed(command.Profile) is null ? await AddAsync(command.Profile, app) : Named(command.Profile)!;
        return await OnTurnAsync(open, async () =>
        {
            open.App = app;
            open.User = command.User;
            return await NavigateAsync(open, write, () => open.Profile.Page.GotoAsync(app.Url("/bff/login").ToString(), new() { Timeout = Milliseconds(Bound) }), login: true);
        });
    }

    private async Task<int> CloseAsync(SittingCommand command, Func<string, Task> write)
    {
        OpenProfile? open;
        lock (gate)
        {
            if (profiles.Remove(command.Profile, out open) is false)
            {
                open = null;
            }
        }

        if (open is null)
        {
            return await FailAsync(write, command, "unknown-profile");
        }

        await EndWatchAsync(open);
        await open.Turn.WaitAsync(stop);
        try
        {
            await open.Profile.DisposeAsync();
        }
        finally
        {
            open.Turn.Release();
        }

        await write(Observations.Closed(DateTimeOffset.UtcNow, open.Name, open.App.AppId));
        return 0;
    }

    /// <summary>The commands on one open profile's page, taken in turn with its watch's ticks.</summary>
    private async Task<int> OnProfileAsync(SittingCommand command, Func<string, Task> write)
    {
        if (Named(command.Profile) is not { } open)
        {
            return await FailAsync(write, command, "unknown-profile");
        }

        return await OnTurnAsync(open, async () =>
        {
            IPage page = open.Profile.Page;
            HarnessApp app = open.App;
            switch (command.Verb)
            {
                case SittingCommand.Reload:
                    return await NavigateAsync(open, write, () => page.ReloadAsync(new() { Timeout = Milliseconds(Bound) }), login: false);

                case SittingCommand.Marker:
                    try
                    {
                        string text = await Redacted.PlaywrightAsync(() => page.Locator("#marker").InnerTextAsync(new() { Timeout = Milliseconds(Bound) }));
                        await EmitAsync(open, write, Observations.Marker(DateTimeOffset.UtcNow, open.Name, app.AppId, text), "marker");
                        return 0;
                    }
                    catch (TimeoutException)
                    {
                        await EmitAsync(open, write, Observations.Timeout(DateTimeOffset.UtcNow, open.Name, app.AppId), Observations.TimeoutToken);
                        return 1;
                    }

                case SittingCommand.Session:
                {
                    FetchResult result = await open.Profile.FetchAsync(app.Url("/bff/session").ToString());
                    await EmitAsync(open, write, Observations.Session(DateTimeOffset.UtcNow, open.Name, app.AppId, result.Status, ProblemOf(result)), "session");
                    return 0;
                }

                case SittingCommand.Api:
                {
                    string path = $"/api/{command.ApiPath}";
                    FetchResult result = await open.Profile.FetchAsync(app.Url(path).ToString());
                    await EmitAsync(open, write, Observations.Api(DateTimeOffset.UtcNow, open.Name, app.AppId, path, result.Status, ProblemOf(result)), "api");
                    return 0;
                }

                default:
                {
                    int removed = await ClearProviderCookiesAsync(open.Profile);
                    await EmitAsync(open, write, Observations.ClearProvider(DateTimeOffset.UtcNow, open.Name, app.AppId, removed), "clear-provider");
                    return 0;
                }
            }
        });
    }

    /// <summary>
    /// The watch's loop (#1024 Q7): each tick at the interval clears the provider's cookies when asked,
    /// reloads and prints the observation, and on <c>login-prompt</c> with <c>--relogin</c> completes the
    /// login and prints that observation too. Each line goes to <see cref="WatchLog"/> and to the caller
    /// that started it, until <c>unwatch</c>, <c>close</c> or <c>stop</c>; a caller that has gone away
    /// leaves the log. A tick that fails ends the watch with a <c>failed</c> line, and that line takes
    /// the same path, so the log says why it stops (#1086).
    /// </summary>
    private async Task<int> WatchAsync(SittingCommand command, Func<string, Task> write)
    {
        if (Named(command.Profile) is not { } open)
        {
            return await FailAsync(write, command, "unknown-profile");
        }

        Watching? watching = null;
        lock (gate)
        {
            if (open.Watch is null)
            {
                watching = new();
                open.Watch = watching;
            }
        }

        if (watching is null)
        {
            return await FailAsync(write, command, "already-watching");
        }

        bool callerGone = false;
        async Task Tell(string line)
        {
            await AppendWatchLogAsync(line);
            if (!callerGone)
            {
                try
                {
                    await write(line);
                }
                catch (Exception gone) when (gone is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    callerGone = true;
                }
            }
        }

        try
        {
            using PeriodicTimer timer = new(command.Every);
            while (await NextTickAsync(timer, watching.Stop.Token))
            {
                await OnTurnAsync(open, async () =>
                {
                    if (command.ClearProvider)
                    {
                        await ClearProviderCookiesAsync(open.Profile);
                    }

                    string? token = null;
                    await NavigateAsync(open, async line => { token = ObservationLine.Head(line)?.Token; await Tell(line); },
                        () => open.Profile.Page.ReloadAsync(new() { Timeout = Milliseconds(Bound) }), login: false);
                    if (command.Relogin && token == Observations.LoginPromptToken)
                    {
                        await NavigateAsync(open, Tell, () => Task.FromResult<IResponse?>(null), login: true);
                    }

                    return 0;
                });
                Interlocked.Increment(ref watching.Ticks);
            }
        }
        catch (Exception failed) when (FailureOf(failed) is { } failure)
        {
            return await FailAsync(Tell, command, failure.Reason, failure.Text);
        }
        finally
        {
            watching.Ended.TrySetResult();
        }

        return 0;
    }

    private async Task<int> UnwatchAsync(SittingCommand command, Func<string, Task> write)
    {
        if (Named(command.Profile) is not { } open)
        {
            return await FailAsync(write, command, "unknown-profile");
        }

        if (await EndWatchAsync(open) is not { } ticks)
        {
            return await FailAsync(write, command, "not-watching");
        }

        await EmitAsync(open, write, Observations.Unwatched(DateTimeOffset.UtcNow, open.Name, open.App.AppId, ticks), "unwatched");
        return 0;
    }

    /// <summary>Ends the profile's watch, if it has one, once its current tick is done; returns its tick count.</summary>
    private async Task<int?> EndWatchAsync(OpenProfile open)
    {
        Watching? watching;
        lock (gate)
        {
            watching = open.Watch;
            open.Watch = null;
        }

        if (watching is null)
        {
            return null;
        }

        await watching.Stop.CancelAsync();
        await watching.Ended.Task;
        watching.Stop.Dispose();
        return watching.Ticks;
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// A navigation and what the page then showed, as one observation (#1024 Q9). A navigation that
    /// failed before a response is <c>tunnel-down</c>, a 404 is <c>not-found</c>; otherwise the page is
    /// watched until it is signed in, at the provider's login, or settled on anything else
    /// (<c>other</c>), and <see cref="Bound"/> passing first is <c>timeout</c>. With
    /// <paramref name="login"/> the stack completes the provider's part first, as the login's user.
    /// </summary>
    private async Task<int> NavigateAsync(OpenProfile open, Func<string, Task> write, Func<Task<IResponse?>> navigate, bool login)
    {
        HarnessApp app = open.App;
        try
        {
            IResponse? response = await Redacted.PlaywrightAsync(navigate);
            if (response?.Status == 404)
            {
                await EmitAsync(open, write, Observations.NotFound(DateTimeOffset.UtcNow, open.Name, app.AppId, await ReadinessAsync(open)), Observations.NotFoundToken);
                return 0;
            }

            if (login)
            {
                await loginTurn.WaitAsync(stop);
                try
                {
                    // The stack's user is the stack's, and the password is read at the first login per user (#1024 Q6).
                    stack.SignInAs(open.User ?? SittingCommand.Users[0]);
                    await stack.CompleteLoginCancellableAsync(open.Profile, app, stop);
                }
                finally
                {
                    loginTurn.Release();
                }
            }
        }
        catch (TimeoutException)
        {
            await EmitAsync(open, write, Observations.Timeout(DateTimeOffset.UtcNow, open.Name, app.AppId), Observations.TimeoutToken);
            return 1;
        }
        catch (PlaywrightException failed) when (failed.Message.Contains("net::ERR_", StringComparison.Ordinal))
        {
            // Chromium names a navigation that failed before a response by its net error, net::ERR_<NAME>
            // (https://source.chromium.org/chromium/chromium/src/+/main:net/base/net_error_list.h), and
            // Playwright's goto and reload throw on such a navigation with that name in the message
            // (https://playwright.dev/dotnet/docs/api/class-page#page-goto).
            await EmitAsync(open, write, Observations.TunnelDown(DateTimeOffset.UtcNow, open.Name, app.AppId), Observations.TunnelDownToken);
            return 0;
        }

        return await SettleAsync(open, write);
    }

    private async Task<int> SettleAsync(OpenProfile open, Func<string, Task> write)
    {
        HarnessApp app = open.App;
        IPage page = open.Profile.Page;
        Stopwatch waited = Stopwatch.StartNew();
        (Seen Seen, TimeSpan Since)? candidate = null;
        while (waited.Elapsed < Bound)
        {
            stop.ThrowIfCancellationRequested();
            Seen seen = await LookAsync(page, app);
            if (seen.SignedIn)
            {
                await EmitAsync(open, write, Observations.SignedIn(DateTimeOffset.UtcNow, open.Name, app.AppId, open.User, await ReadinessAsync(open)), Observations.SignedInToken);
                return 0;
            }

            if (seen.Prompt)
            {
                await EmitAsync(open, write, Observations.LoginPrompt(DateTimeOffset.UtcNow, open.Name, app.AppId, Readiness.Unknown), Observations.LoginPromptToken);
                return 0;
            }

            if (!seen.Settled)
            {
                candidate = null;
            }
            else if (candidate is not { } held || held.Seen != seen)
            {
                candidate = (seen, waited.Elapsed);
            }
            else if (waited.Elapsed - held.Since >= Settle)
            {
                Readiness ready = seen.Host == app.AppId ? await ReadinessAsync(open) : Readiness.Unknown;
                DateTimeOffset at = DateTimeOffset.UtcNow;
                await WritePageAsync(page, at);
                await EmitAsync(open, write, Observations.Other(at, open.Name, app.AppId, seen.Host, seen.Path, ready, seen.Shell), Observations.OtherToken);
                return 0;
            }

            await Task.Delay(100, stop);
        }

        await EmitAsync(open, write, Observations.Timeout(DateTimeOffset.UtcNow, open.Name, app.AppId), Observations.TimeoutToken);
        return 1;
    }

    /// <summary>What the page shows now, read without printing it: where it is as an app id, <c>provider</c> or <c>elsewhere</c>, and the shell's state on an app host.</summary>
    private async Task<Seen> LookAsync(IPage page, HarnessApp app)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out Uri? url))
        {
            return new Seen("elsewhere", "-", null, Settled: false, SignedIn: false, Prompt: false);
        }

        string path = url.AbsolutePath.Length > 0 && !url.AbsolutePath.Any(char.IsWhiteSpace) ? url.AbsolutePath : "-";
        try
        {
            if (string.Equals(url.Host, app.Origin.Host, StringComparison.OrdinalIgnoreCase))
            {
                string? shell = await page.EvaluateAsync<string?>("() => document.querySelector('#session-state')?.textContent ?? null");
                shell = shell is null ? null : Redacted.Text(shell, []);
                bool signedIn = path == "/" && shell == Profile.SignedInState;
                return new Seen(app.AppId, path, shell, Settled: shell is not "loading", signedIn, Prompt: false);
            }

            if (string.Equals(url.Host, stack.ProviderHost, StringComparison.OrdinalIgnoreCase))
            {
                bool prompt = await page.Locator("input[name='username']:visible, input[name='password']:visible").CountAsync() > 0;
                return new Seen("provider", path, null, Settled: true, SignedIn: false, prompt);
            }
        }
        catch (PlaywrightException)
        {
            // The page navigated while it was read: not settled yet.
            return new Seen("elsewhere", path, null, Settled: false, SignedIn: false, Prompt: false);
        }

        return new Seen("elsewhere", path, null, Settled: true, SignedIn: false, Prompt: false);
    }

    /// <summary>The paired <c>/readyz</c> probe, a same-origin page fetch, on an app host only (#1024 Q4).</summary>
    private static async Task<Readiness> ReadinessAsync(OpenProfile open)
    {
        try
        {
            if (!Uri.TryCreate(open.Profile.Page.Url, UriKind.Absolute, out Uri? url) || !string.Equals(url.Host, open.App.Origin.Host, StringComparison.OrdinalIgnoreCase))
            {
                return Readiness.Unknown;
            }

            FetchResult probe = await open.Profile.FetchAsync("/readyz");
            if (probe.Status != 200)
            {
                return Readiness.Unknown;
            }

            using JsonDocument body = JsonDocument.Parse(probe.Body);
            string revision = body.RootElement.GetProperty("revision").GetRawText();
            string? instance = body.RootElement.GetProperty("instance").GetString();
            return Digits().IsMatch(revision) && instance is not null && InstancePattern().IsMatch(instance)
                ? new Readiness(revision, instance)
                : Readiness.Unknown;
        }
        catch (Exception failed) when (failed is PlaywrightException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Readiness.Unknown;
        }
    }

    /// <summary>The <c>code</c> of a problem body, when it has the shape of one; never the body.</summary>
    private static string? ProblemOf(FetchResult result) => result.Code is { } code && ProblemPattern().IsMatch(code) ? code : null;

    /// <summary>
    /// Removes the cookies whose domain is the provider's host, with or without a leading dot, and
    /// returns how many went: the context's count before less its count after, so the app's cookies,
    /// which stay, are not counted.
    /// </summary>
    private async Task<int> ClearProviderCookiesAsync(Profile profile)
    {
        IReadOnlyList<BrowserContextCookiesResult> before = await profile.Context.CookiesAsync();
        foreach (string domain in before.Select(cookie => cookie.Domain)
            .Where(domain => string.Equals(domain.TrimStart('.'), stack.ProviderHost, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal))
        {
            await profile.Context.ClearCookiesAsync(new() { Domain = domain });
        }

        return before.Count - (await profile.Context.CookiesAsync()).Count;
    }

    /// <summary>An <c>other</c> page's query-less URL and visible text, through the scrub a timed-out hold's evidence uses (#1003).</summary>
    private async Task WritePageAsync(IPage page, DateTimeOffset at)
    {
        PageShown shown = await NavigationHold.ShownAsync(page);
        string directory = Path.Combine(bundle, PagesDirectory);
        Directory.CreateDirectory(directory);
        string name = ObservationLine.Time(at);
        string path = Path.Combine(directory, $"{name}.txt");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"{name}-{n.ToString(CultureInfo.InvariantCulture)}.txt");
        }

        await WritePrivateAsync(path, $"url: {shown.Url}\n\n{shown.Text}\n", append: false);
    }

    private async Task AppendWatchLogAsync(string line)
    {
        await watchLogTurn.WaitAsync(stop);
        try
        {
            await WritePrivateAsync(Path.Combine(bundle, WatchLog), line + "\n", append: true);
        }
        finally
        {
            watchLogTurn.Release();
        }
    }

    /// <summary>A file only its owner reads (mode 600 on Unix), as the bundle's other files are (the wiz skill's rule 6).</summary>
    internal static async Task WritePrivateAsync(string path, string text, bool append)
    {
        FileStreamOptions options = new()
        {
            Mode = append ? FileMode.Append : FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using FileStream file = new(path, options);
        await using StreamWriter writer = new(file);
        await writer.WriteAsync(text);
    }

    private async Task<OpenProfile> AddAsync(string name, HarnessApp app)
    {
        Profile profile = await stack.OpenCancellableAsync(stop);
        OpenProfile open = new(name, profile) { App = app };
        lock (gate)
        {
            profiles[name] = open;
        }

        return open;
    }

    private OpenProfile? Named(string name)
    {
        lock (gate)
        {
            return profiles.GetValueOrDefault(name);
        }
    }

    private HarnessApp? AppOf(string appId)
    {
        try
        {
            return stack.App(appId);
        }
        catch (Exception unknown) when (unknown is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<int> OnTurnAsync(OpenProfile open, Func<Task<int>> work)
    {
        await open.Turn.WaitAsync(stop);
        try
        {
            return await work();
        }
        finally
        {
            open.Turn.Release();
        }
    }

    private static async Task EmitAsync(OpenProfile open, Func<string, Task> write, string line, string token)
    {
        open.Last = token;
        open.LastAt = DateTimeOffset.UtcNow;
        await write(line);
    }

    private static async Task<int> FailAsync(Func<string, Task> write, SittingCommand command, string reason, string? text = null)
    {
        await write(Observations.Failed(DateTimeOffset.UtcNow, command.Profile, command.Verb, reason, text));
        return 1;
    }

    private static float Milliseconds(TimeSpan span) => (float)span.TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await StopAsync(_ => Task.CompletedTask);
        }
        finally
        {
            await stack.DisposeAsync();
            loginTurn.Dispose();
            watchLogTurn.Dispose();
        }
    }

    [GeneratedRegex("^[0-9]{1,18}$", RegexOptions.None, 1000)]
    private static partial Regex Digits();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.None, 1000)]
    private static partial Regex InstancePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.None, 1000)]
    private static partial Regex ProblemPattern();

    /// <summary>What <see cref="LookAsync"/> read; two reads that are equal are the same page showing the same thing.</summary>
    private sealed record Seen(string Host, string Path, string? Shell, bool Settled, bool SignedIn, bool Prompt);

    private sealed class OpenProfile(string name, Profile profile)
    {
        public string Name { get; } = name;

        public Profile Profile { get; } = profile;

        public SemaphoreSlim Turn { get; } = new(1, 1);

        public required HarnessApp App { get; set; }

        public string? User { get; set; }

        public string? Last { get; set; }

        public DateTimeOffset? LastAt { get; set; }

        public Watching? Watch { get; set; }
    }

    private sealed class Watching
    {
        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Ticks;
    }
}
