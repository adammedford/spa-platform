using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// One response to a main-frame navigation request, as the browser received it: its status, its path
/// with every query value and fragment removed (#1005 O1), whether it came from the provider, and the
/// Fetch Metadata the request carried (#961 O13). Nothing else of the request or response is kept.
/// </summary>
public sealed record Hop(int Status, string Path, bool OnProvider, string? Site, string? Mode, string? Dest)
{
    public bool IsRedirect => Status is >= 300 and < 400;
}

/// <summary>One navigation: its hops in order, the path it came to rest on (null when it stopped with an error), and whether the provider showed its form.</summary>
public sealed record Navigation(IReadOnlyList<Hop> Hops, string? Landed, bool Prompted)
{
    /// <summary>The navigation's runs of redirects, split wherever a page interrupted them (#1005 O1).</summary>
    public IReadOnlyList<IReadOnlyList<Redirect>> Runs => FragmentsNavigations.Runs(Hops);
}

/// <summary>A page in one engine's fresh profile, on the cell through the routing proxy.</summary>
public interface IEnginePage : IAsyncDisposable
{
    /// <summary>
    /// Navigates to <paramref name="url"/> and records each hop until the page is on <paramref name="landed"/>
    /// or the provider shows its form; with <paramref name="fill"/> the form is filled as the deployed stack
    /// fills it and the navigation goes on to <paramref name="landed"/>.
    /// </summary>
    Task<Navigation> NavigateAsync(Uri url, bool fill, string landed, CancellationToken ct);

    /// <summary>A script run on the page, as the page's own code would run it, answering JSON.</summary>
    Task<JsonElement> EvaluateAsync(string script, object? argument, CancellationToken ct);

    /// <summary>Drops the cookies the gateway set on the shell's host and keeps the provider's, so the next sign-in meets a live managed-login session.</summary>
    Task ForgetGatewayAsync(CancellationToken ct);
}

/// <summary>One launched engine: its name as <see cref="FragmentsObserver.Engines"/> spells it, its version, and fresh profiles.</summary>
public interface IEngine
{
    string Name { get; }

    string Version { get; }

    Task<IEnginePage> OpenAsync(CancellationToken ct);
}

/// <summary>
/// The engines a row runs in. <see cref="LaunchAsync"/> answers the ones that launched and the reason
/// each other did not: an engine that does not launch is missing from the record, which the observer
/// reports, never a pass on the others.
/// </summary>
public interface IEngines : IAsyncDisposable
{
    Task<(IReadOnlyList<IEngine> Launched, IReadOnlyList<string> Refused)> LaunchAsync(IReadOnlyList<string> names, CancellationToken ct);
}

/// <summary>What the browser rows read from navigations, with no browser: runs, chain hops, Fetch Metadata.</summary>
public static class FragmentsNavigations
{
    /// <summary>Consecutive redirects, split at every hop that is not one (a page); an empty run is dropped.</summary>
    public static IReadOnlyList<IReadOnlyList<Redirect>> Runs(IEnumerable<Hop> hops)
    {
        List<IReadOnlyList<Redirect>> runs = [];
        List<Redirect> run = [];
        foreach (Hop hop in hops)
        {
            if (hop.IsRedirect)
            {
                run.Add(new Redirect(hop.Status, hop.Path));
                continue;
            }

            if (run.Count > 0)
            {
                runs.Add(run);
                run = [];
            }
        }

        if (run.Count > 0)
        {
            runs.Add(run);
        }

        return runs;
    }

    /// <summary>
    /// The navigation's chain hops (#1005 O1 "per hop"): from each <c>/bff/chain</c> step that sends the browser
    /// to the provider, for a fragment's sign-in, up to the next step, each as its runs. A chain at N
    /// fragments has N + 1 steps (src/Platform.Identity/BffFragments.cs): the last finds no fragment left and
    /// redirects to <c>returnUrl</c>, so it is the chain's end, not a hop.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<IReadOnlyList<Redirect>>> ChainHops(Navigation navigation)
    {
        int[] starts = [.. HopStarts(navigation)];
        return [.. starts.Select(start =>
        {
            int end = Enumerable.Range(start + 1, navigation.Hops.Count - start - 1).FirstOrDefault(i => IsStep(navigation.Hops[i]), navigation.Hops.Count);
            return Runs(navigation.Hops.Skip(start).Take(end - start));
        })];
    }

    /// <summary>
    /// The Fetch Metadata each <c>/bff/chain</c> step that starts a hop carried, in order (#961 O13), as the browser
    /// sent it: #1005 O13 says "from its request line", but the gateway's request line carries no
    /// <c>Sec-Fetch-*</c> field (contracts/telemetry.md, "Log fields"), so the request the engine sent is the record.
    /// </summary>
    public static IReadOnlyList<ChainStep> ChainSteps(Navigation navigation) =>
        [.. HopStarts(navigation).Select(i => navigation.Hops[i]).Select(hop => new ChainStep(hop.Site, hop.Mode, hop.Dest))];

    private static IEnumerable<int> HopStarts(Navigation navigation) =>
        Enumerable.Range(0, navigation.Hops.Count).Where(i => IsStep(navigation.Hops[i]) && i + 1 < navigation.Hops.Count && navigation.Hops[i + 1].OnProvider);

    private static bool IsStep(Hop hop) => !hop.OnProvider && hop.Path == FragmentsWorkloads.ChainPath;
}

/// <summary>
/// Chromium, Firefox and WebKit from Microsoft.Playwright, each on the cell through <see cref="RoutingProxy"/>:
/// the app hosts to the tunnel, every other host direct, since Firefox and WebKit take no host-resolver
/// switch. Each trusts what the workstation trusts (Firefox through its enterprise-roots preference),
/// so the ALB's certificate must chain to the authority certs.sh installed. Installed and launched only
/// when a row runs, after its skip.
/// </summary>
public sealed class PlaywrightEngines(DeployedStack stack, HarnessApp shell) : IEngines
{
    private readonly List<IBrowser> browsers = [];
    private IPlaywright? playwright;
    private RoutingProxy? proxy;

    public async Task<(IReadOnlyList<IEngine> Launched, IReadOnlyList<string> Refused)> LaunchAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        int installed = Microsoft.Playwright.Program.Main(["install", .. names]);
        if (installed != 0)
        {
            return ([], [.. names.Select(name => $"{name}: Playwright's installer exited {installed}")]);
        }

        playwright ??= await Playwright.CreateAsync();
        proxy ??= RoutingProxy.Start(stack.Cell.Domain, stack.TunnelLocalPort);
        List<IEngine> launched = [];
        List<string> refused = [];
        foreach (string name in names)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                IBrowser browser = await (name switch
                {
                    "chromium" => playwright.Chromium,
                    "firefox" => playwright.Firefox,
                    "webkit" => playwright.Webkit,
                    _ => throw new ArgumentException($"{name} is no engine Playwright ships", nameof(names)),
                }).LaunchAsync(new()
                {
                    Headless = true,
                    Proxy = new() { Server = $"http://127.0.0.1:{proxy.Port}" },
                    FirefoxUserPrefs = name == "firefox" ? new Dictionary<string, object> { ["security.enterprise_roots.enabled"] = true } : null,
                });
                browsers.Add(browser);
                launched.Add(new Engine(name, browser, stack, shell));
            }
            catch (PlaywrightException failed)
            {
                // The launch error names the engine's executable and profile directory, never a URL of the cell.
                string reason = $"{name}: {Redacted.Text(failed.Message.Split('\n')[0], [])}";
                refused.Add(reason);
                Console.WriteLine($"not launched: {reason}");
            }
        }

        return (launched, refused);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IBrowser browser in browsers)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
        proxy?.Dispose();
    }

    private sealed class Engine(string name, IBrowser browser, DeployedStack stack, HarnessApp shell) : IEngine
    {
        public string Name => name;

        public string Version => browser.Version;

        public async Task<IEnginePage> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = false });
            return new EnginePage(context, await context.NewPageAsync(), stack, shell);
        }
    }

    private sealed class EnginePage(IBrowserContext context, IPage page, DeployedStack stack, HarnessApp shell) : IEnginePage
    {
        public async Task<Navigation> NavigateAsync(Uri url, bool fill, string landed, CancellationToken ct)
        {
            List<Hop> hops = [];
            void Seen(object? sender, IResponse response)
            {
                if (response.Request.IsNavigationRequest && response.Frame == page.MainFrame)
                {
                    Uri at = new(response.Url);
                    // Request.Headers "does not return security-related headers, including cookie-related ones"
                    // (https://playwright.dev/dotnet/docs/api/class-request#request-headers); the complete set is
                    // AllHeadersAsync's (#request-all-headers), which this synchronous event handler does not await (an
                    // async one would be async void). Whether an engine counts Sec-Fetch-* among them is not documented;
                    // #1411's session found that Chromium and WebKit "report redirect responses and Sec-Fetch-*" here
                    // (comment 7149835958). The choice fails closed: an omitted header is null, and
                    // FragmentsObserver.Chain reports a step with no Fetch Metadata recorded as not proved.
                    IReadOnlyDictionary<string, string> headers = response.Request.Headers;
                    lock (hops)
                    {
                        hops.Add(new Hop(response.Status, at.AbsolutePath, at.Host == stack.ProviderHost,
                            headers.GetValueOrDefault("sec-fetch-site"), headers.GetValueOrDefault("sec-fetch-mode"), headers.GetValueOrDefault("sec-fetch-dest")));
                    }
                }
            }

            page.Response += Seen;
            bool prompted = false;
            try
            {
                string target = new Uri(url, landed).ToString();
                await Redacted.PlaywrightAsync(() => page.GotoAsync(url.ToString()));
                await Redacted.PlaywrightAsync(() => page.WaitForURLAsync(at => at == target || new Uri(at).Host == stack.ProviderHost));
                if (new Uri(page.Url).Host == stack.ProviderHost)
                {
                    prompted = await page.Locator("input[name='username']:visible").First.IsVisibleAsync()
                        || await Redacted.PlaywrightAsync(async () =>
                        {
                            await page.Locator("input[name='username']:visible").First.WaitForAsync(new() { Timeout = 10_000 });
                            return true;
                        });
                    if (!fill)
                    {
                        return new Navigation(Snapshot(hops), null, prompted);
                    }

                    await stack.CompleteLoginCancellableAsync(new Profile(stack, context, page), shell, ct);
                    await Redacted.PlaywrightAsync(() => page.WaitForURLAsync(target));
                }

                return new Navigation(Snapshot(hops), new Uri(page.Url).AbsolutePath, prompted);
            }
            catch (PlaywrightException)
            {
                return new Navigation(Snapshot(hops), null, prompted);
            }
            finally
            {
                page.Response -= Seen;
            }
        }

        public async Task<JsonElement> EvaluateAsync(string script, object? argument, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return await Redacted.PlaywrightAsync(() => page.EvaluateAsync<JsonElement>(script, argument));
        }

        public async Task ForgetGatewayAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await context.ClearCookiesAsync(new() { Domain = shell.Origin.Host });
        }

        private static Hop[] Snapshot(List<Hop> hops)
        {
            lock (hops)
            {
                return [.. hops];
            }
        }

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}

/// <summary>
/// An HTTP CONNECT proxy on loopback: a tunnel to a host under the cell's domain goes to the cell's tunnel
/// port on loopback, and every other to the host itself. It sees only the CONNECT line; TLS runs end to
/// end inside it, so it reads no request, cookie or token. A plain-HTTP request to loopback, which only
/// O1's boundary probe sends, is forwarded to its port as it stands.
/// </summary>
public sealed class RoutingProxy : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();

    private RoutingProxy(TcpListener listener) => this.listener = listener;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Where a CONNECT to <paramref name="host"/> goes: the tunnel for the cell's hosts, the host itself for any other.</summary>
    public static (string Host, int Port) Route(string host, int port, string domain, int tunnelPort) =>
        host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase) ? ("127.0.0.1", tunnelPort) : (host, port);

    public static RoutingProxy Start(string domain, int tunnelPort)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        RoutingProxy proxy = new(listener);
        _ = Task.Run(() => proxy.AcceptAsync(domain, tunnelPort));
        return proxy;
    }

    private async Task AcceptAsync(string domain, int tunnelPort)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception ended) when (ended is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => TunnelAsync(client, domain, tunnelPort));
        }
    }

    private async Task TunnelAsync(TcpClient client, string domain, int tunnelPort)
    {
        using (client)
        {
            try
            {
                NetworkStream down = client.GetStream();
                string head = await HeadAsync(down, stop.Token);
                string[] line = head.Split(' ', 3);
                if (line is [_, string absolute, _] && Uri.TryCreate(absolute, UriKind.Absolute, out Uri? plain) && plain.Scheme == "http" && plain.IsLoopback)
                {
                    await ForwardAsync(down, head, absolute, plain);
                    return;
                }

                string authority = line is ["CONNECT", string named, _] ? named : "";
                int colon = authority.LastIndexOf(':');
                if (colon < 1 || !int.TryParse(authority[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int port))
                {
                    await down.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop.Token);
                    return;
                }

                (string host, int to) = Route(authority[..colon], port, domain, tunnelPort);
                using TcpClient upstream = new();
                await upstream.ConnectAsync(host, to, stop.Token);
                await down.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop.Token);
                NetworkStream up = upstream.GetStream();
                await Task.WhenAny(down.CopyToAsync(up, stop.Token), up.CopyToAsync(down, stop.Token));
            }
            catch (Exception closed) when (closed is IOException or SocketException or OperationCanceledException)
            {
                // A tunnel ends when either side closes; nothing of it is printed.
            }
        }
    }

    /// <summary>
    /// A plain-HTTP request to loopback, as an engine that proxies loopback sends it (O1's boundary probe):
    /// the head sent on in origin form, then the two directions piped.
    /// </summary>
    private async Task ForwardAsync(NetworkStream down, string head, string absolute, Uri target)
    {
        using TcpClient upstream = new();
        await upstream.ConnectAsync(IPAddress.Loopback, target.Port, stop.Token);
        NetworkStream up = upstream.GetStream();
        int first = head.IndexOf(absolute, StringComparison.Ordinal);
        await up.WriteAsync(Encoding.ASCII.GetBytes(head[..first] + target.PathAndQuery + head[(first + absolute.Length)..]), stop.Token);
        await Task.WhenAny(down.CopyToAsync(up, stop.Token), up.CopyToAsync(down, stop.Token));
    }

    /// <summary>The request head, read a byte at a time up to its blank line, at most 8 KiB.</summary>
    private static async Task<string> HeadAsync(NetworkStream stream, CancellationToken ct)
    {
        StringBuilder head = new();
        byte[] one = new byte[1];
        while (head.Length < 8192 && await stream.ReadAsync(one, ct) == 1)
        {
            head.Append((char)one[0]);
            if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }
        }

        return head.ToString();
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
