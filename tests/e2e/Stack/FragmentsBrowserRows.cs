using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The browser rows' drives (#1005 O1, O8, O12, O13's chain), over <see cref="IEngines"/>, so a test runs
/// each against scripted engines and the driver against Chromium, Firefox and WebKit on the cell. O1
/// pulls the one lever it needs, the shell's entry at each N, through spactl and puts it back
/// (#1411's decision of 2026-11-24).
/// </summary>
public sealed class FragmentsBrowserRows(IEngines engines, HarnessAppUrl shell)
{
    /// <summary>The path every sign-in returns to: the shell's root, which <c>returnUrl</c> names.</summary>
    public const string ReturnUrl = "/";

    /// <summary>The range O1's boundary probe walks: the Fetch standard's 20 inside it.</summary>
    public static readonly IReadOnlyList<int> ProbeRange = [.. Enumerable.Range(16, 9)];

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Each engine that launched, by the name and version a record names its user agent with.</summary>
    public static string AgentOf(IEngine engine) => $"{engine.Name} {engine.Version}";

    private Uri Login(string? fragment = null) =>
        shell.Url(fragment is null ? "/bff/login?returnUrl=%2F" : $"/bff/login?fragment={Uri.EscapeDataString(fragment)}&returnUrl=%2F");

    /// <summary>O13's chain (F-42): in each engine, one interactive sign-in, each hop's step with its Fetch Metadata, and whether it reached <c>returnUrl</c>.</summary>
    public async Task<(ChainRecord Record, IReadOnlyList<string> Agents, IReadOnlyList<string> Refused)> ChainAsync(CancellationToken ct)
    {
        (IReadOnlyList<IEngine> launched, IReadOnlyList<string> refused) = await engines.LaunchAsync(FragmentsObserver.Engines, ct);
        List<ChainRun> runs = [];
        foreach (IEngine engine in launched)
        {
            await using IEnginePage page = await engine.OpenAsync(ct);
            Navigation navigation = await page.NavigateAsync(Login(), fill: true, ReturnUrl, ct);
            runs.Add(new ChainRun(AgentOf(engine), FragmentsNavigations.ChainSteps(navigation), navigation.Landed == ReturnUrl));
        }

        return (new ChainRecord(runs), [.. launched.Select(AgentOf)], refused);
    }

    /// <summary>
    /// O12 (F-41) in the first engine that launched: signed in on the shell, then <c>import()</c> of a fragment's
    /// entry at <c>entry.js</c> and of one at another name, each with the path of the chunk it fetched under
    /// its fragment.
    /// </summary>
    public async Task<(EntryImportRecord Record, IReadOnlyList<string> Agents)> EntryImportsAsync(IReadOnlyList<LiveFragment> fragments, CancellationToken ct)
    {
        (IReadOnlyList<IEngine> launched, _) = await engines.LaunchAsync(["chromium"], ct);
        if (launched.Count == 0)
        {
            return (new EntryImportRecord([]), []);
        }

        LiveFragment[] cases = [.. new[]
        {
            fragments.FirstOrDefault(fragment => fragment.Entry == PlatformPaths.DefaultEntry),
            fragments.FirstOrDefault(fragment => fragment.Entry != PlatformPaths.DefaultEntry),
        }.OfType<LiveFragment>()];
        await using IEnginePage page = await launched[0].OpenAsync(ct);
        Navigation signedIn = await page.NavigateAsync(Login(), fill: true, ReturnUrl, ct);
        List<EntryImport> imports = [];
        foreach (LiveFragment fragment in cases)
        {
            if (signedIn.Landed != ReturnUrl)
            {
                imports.Add(new EntryImport(fragment.FragmentId, fragment.Entry, null, null));
                continue;
            }

            JsonElement answer = await page.EvaluateAsync(ImportScript, new[] { fragment.FragmentId, fragment.Entry }, ct);
            imports.Add(new EntryImport(fragment.FragmentId, fragment.Entry,
                answer.GetProperty("loaded").GetBoolean(),
                answer.GetProperty("chunk").ValueKind == JsonValueKind.String ? answer.GetProperty("chunk").GetString() : null));
        }

        return (new EntryImportRecord(imports), [AgentOf(launched[0])]);
    }

    /// <summary>
    /// The page's own <c>import()</c> of the entry, then the first resource the browser fetched under the
    /// fragment's path that is not the entry: the relative chunk, by its path alone
    /// (https://www.w3.org/TR/resource-timing/).
    /// </summary>
    public const string ImportScript = """
        async ([id, entry]) => {
          const root = `/fragments/${id}/`;
          try { await import(root + entry); } catch { return { loaded: false, chunk: null }; }
          const chunk = performance.getEntriesByType("resource").map(each => new URL(each.name).pathname)
            .find(path => path.startsWith(root) && path !== root + entry);
          return { loaded: true, chunk: chunk ?? null };
        }
        """;

    /// <summary>
    /// O8 (F-40) in the first engine that launched: an interactive sign-in, then the fragment's
    /// <c>/bff/login?fragment=</c> inside the managed-login hour, with no form filled, and again once the
    /// hour and two minutes have passed since the sign-in.
    /// </summary>
    public async Task<(FallbackRecord Record, IReadOnlyList<string> Agents)> FallbackAsync(LiveFragment fragment, CancellationToken ct)
    {
        (IReadOnlyList<IEngine> launched, _) = await engines.LaunchAsync(["chromium"], ct);
        if (launched.Count == 0)
        {
            return (new FallbackRecord(null, null, null), []);
        }

        await using IEnginePage page = await launched[0].OpenAsync(ct);
        Navigation signIn = await page.NavigateAsync(Login(), fill: true, ReturnUrl, ct);
        if (signIn.Landed != ReturnUrl || !signIn.Prompted)
        {
            return (new FallbackRecord(null, null, null), [AgentOf(launched[0])]);
        }

        DateTimeOffset signedIn = Clock.GetUtcNow();
        FallbackHalf inside = await HalfAsync(page, fragment, ct);
        await Delay(signedIn + FragmentsBounds.ManagedLoginHour + TimeSpan.FromMinutes(2) - Clock.GetUtcNow(), ct);
        FallbackHalf after = await HalfAsync(page, fragment, ct);
        return (new FallbackRecord(signedIn, inside, after), [AgentOf(launched[0])]);
    }

    private async Task<FallbackHalf> HalfAsync(IEnginePage page, LiveFragment fragment, CancellationToken ct)
    {
        DateTimeOffset at = Clock.GetUtcNow();
        Navigation fallback = await page.NavigateAsync(Login(fragment.FragmentId), fill: false, ReturnUrl, ct);
        bool ended = fallback.Landed == ReturnUrl || fallback.Prompted;
        return new FallbackHalf(at, ended ? fallback.Prompted : null, ended ? fallback.Prompted : null);
    }

    /// <summary>
    /// O1: for each N from 0 to 6, the shell's definition with its first N fragments written by
    /// <c>spactl app add</c>, a wait for the cell to apply it, then in each engine an interactive sign-in and,
    /// with the managed-login session live and the gateway's cookies dropped, an uninterrupted one, whose
    /// chain hops are recorded and whose end must be <c>returnUrl</c>. Each engine's boundary probe follows
    /// a loopback redirect chain across <see cref="ProbeRange"/>. The shell's entry is written back at the N it
    /// had, whatever happened.
    /// </summary>
    public async Task<RedirectRecord> RedirectsAsync(O1Lever lever, CancellationToken ct)
    {
        (IReadOnlyList<IEngine> launched, _) = await engines.LaunchAsync(FragmentsObserver.Engines, ct);
        Dictionary<string, List<RedirectRun>> runs = launched.ToDictionary(engine => engine.Name, _ => new List<RedirectRun>(), StringComparer.Ordinal);
        return await Restoring.RunAsync(async () =>
        {
            for (int n = 0; n <= FragmentsObserver.MaxN; n++)
            {
                await lever.ActivateAsync(n, ct);
                await Delay(lever.Propagation, ct);
                foreach (IEngine engine in launched)
                {
                    runs[engine.Name].Add(await RunAsync(engine, n, ct));
                }
            }

            List<EngineRedirects> engineRecords = [];
            foreach (IEngine engine in launched)
            {
                engineRecords.Add(new EngineRedirects(engine.Name, engine.Version, runs[engine.Name], await ProbeAsync(engine, ct)));
            }

            return new RedirectRecord(engineRecords);
        }, bound => lever.ActivateAsync(lever.StartN, bound), lever.Restore);
    }

    private async Task<RedirectRun> RunAsync(IEngine engine, int n, CancellationToken ct)
    {
        await using IEnginePage page = await engine.OpenAsync(ct);
        // The interactive sign-in leaves the managed-login session live once its form is submitted, even when its
        // chain then stops at the redirect limit, so the uninterrupted run follows it whatever it landed on.
        Navigation interactive = await page.NavigateAsync(Login(), fill: true, ReturnUrl, ct);
        await page.ForgetGatewayAsync(ct);
        Navigation uninterrupted = await page.NavigateAsync(Login(), fill: false, ReturnUrl, ct);
        return new RedirectRun(n, uninterrupted.Runs, interactive.Runs, FragmentsNavigations.ChainHops(uninterrupted), uninterrupted.Landed == ReturnUrl);
    }

    /// <summary>The most redirects the engine followed and the fewest at which it stopped, over a loopback chain of each length in <see cref="ProbeRange"/>.</summary>
    private static async Task<BoundaryProbe?> ProbeAsync(IEngine engine, CancellationToken ct)
    {
        using RedirectChain chain = RedirectChain.Start();
        await using IEnginePage page = await engine.OpenAsync(ct);
        int? followed = null;
        int? stopped = null;
        foreach (int length in ProbeRange)
        {
            Navigation walked = await page.NavigateAsync(chain.Url(length), fill: false, RedirectChain.End, ct);
            if (walked.Landed == RedirectChain.End)
            {
                followed = length;
            }
            else
            {
                stopped ??= length;
            }
        }

        return followed is { } most && stopped is { } least ? new BoundaryProbe(most, least) : null;
    }
}

/// <summary>
/// O1's lever: the shell's definition (fixtures/fragments/&lt;shell&gt;.json, the one the seed's fragment step
/// writes) with its first N fragments, written by <c>spactl app add</c> with the releases the live registry
/// already gives it, and the N to put back.
/// </summary>
public sealed class O1Lever(SpactlRun spactl, IObjectStore store, JsonObject definition, int startN, string config, string outputs, TimeSpan propagation)
{
    public int StartN => startN;

    public TimeSpan Propagation => propagation + TimeSpan.FromSeconds(30);

    public TimeSpan Restore => TimeSpan.FromMinutes(5);

    /// <summary>The commands' definitions, for a test that reads what each N wrote.</summary>
    public List<string> Written { get; } = [];

    public async Task ActivateAsync(int n, CancellationToken ct)
    {
        JsonObject truncated = (JsonObject)definition.DeepClone();
        if (truncated["fragments"] is JsonArray fragments)
        {
            while (fragments.Count > n)
            {
                fragments.RemoveAt(fragments.Count - 1);
            }

            if (n == 0)
            {
                truncated.Remove("fragments");
            }
        }

        string directory = Directory.CreateTempSubdirectory("spa-e2e-o1-").FullName;
        try
        {
            string path = Path.Combine(directory, "definition.json");
            string text = truncated.ToJsonString();
            await File.WriteAllTextAsync(path, text, ct);
            Written.Add(text);
            int? exit = await spactl(["app", "add", path, "--config", config, "--outputs", outputs], store, ct);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"spactl app add of the shell at N = {n.ToString(CultureInfo.InvariantCulture)} exited {exit?.ToString(CultureInfo.InvariantCulture) ?? "by an exception"}; O1 needs Policy:FragmentsPerShell raised to 6 for its run (#1011 row 1).");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>
/// A redirect chain on loopback for O1's boundary probe: <c>/r/{k}</c> answers 302 to <c>/r/{k - 1}</c>, and
/// <c>/r/0</c> a page. The routing proxy forwards a plain-HTTP request to loopback as it stands.
/// </summary>
public sealed class RedirectChain : IDisposable
{
    public const string End = "/r/0";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();

    private RedirectChain(TcpListener listener) => this.listener = listener;

    public Uri Url(int length) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/r/{length.ToString(CultureInfo.InvariantCulture)}");

    public static RedirectChain Start()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        RedirectChain chain = new(listener);
        _ = Task.Run(chain.ServeAsync);
        return chain;
    }

    /// <summary>The answer to one request line's target: a redirect one shorter, or the end's page.</summary>
    public static string Answer(string target) =>
        target.StartsWith("/r/", StringComparison.Ordinal) && int.TryParse(target[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int k) && k > 0
            ? $"HTTP/1.1 302 Found\r\nLocation: /r/{(k - 1).ToString(CultureInfo.InvariantCulture)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 21\r\nConnection: close\r\n\r\n<!doctype html><p>end";

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer, stop.Token);
                string[] line = Encoding.ASCII.GetString(buffer, 0, read).Split(' ', 3);
                string target = line.Length > 1 ? (Uri.TryCreate(line[1], UriKind.Absolute, out Uri? absolute) ? absolute.AbsolutePath : line[1]) : "/";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(Answer(target)), stop.Token);
            }
            catch (Exception ended) when (ended is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
