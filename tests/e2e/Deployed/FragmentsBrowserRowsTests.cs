using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The browser rows' drives (<see cref="FragmentsBrowserRows"/>) against scripted engines that answer each
/// navigation as Cognito and the gateway would at the shell's current N, the routing proxy against a
/// loopback listener, and O1's lever against a recording of spactl. No browser, no AWS. Each test names
/// in its body the mutation that turns it red.
/// </summary>
public sealed class FragmentsBrowserRowsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HarnessAppUrl Shell = new(new Uri("https://shell.example.test:8443/"));

    private static Hop App(int status, string path, string? site = null) => new(status, path, false, site, site is null ? null : "navigate", site is null ? null : "document");

    private static Hop Provider(int status, string path) => new(status, path, true, "cross-site", "navigate", "document");

    /// <summary>A sign-in at N as the cell answers it: the provider's form when <paramref name="prompt"/>, each fragment's hop, then the chain's last step to returnUrl.</summary>
    private static Navigation SignIn(int n, bool prompt)
    {
        List<Hop> hops = [App(302, "/bff/login"), Provider(302, "/oauth2/authorize")];
        if (prompt)
        {
            hops.AddRange([Provider(302, "/login"), Provider(200, "/login"), Provider(302, "/login")]);
        }

        hops.Add(App(302, "/signin-oidc", "cross-site"));
        for (int i = 1; i <= n; i++)
        {
            hops.AddRange([App(302, "/bff/chain", "cross-site"), Provider(302, "/oauth2/authorize"), App(302, $"/signin-oidc/f{i}", "cross-site")]);
        }

        if (n > 0)
        {
            hops.Add(App(302, "/bff/chain", "cross-site"));
        }

        hops.Add(App(200, "/"));
        int redirects = 0;
        int cut = hops.FindIndex(hop => (redirects = hop.IsRedirect ? redirects + 1 : 0) > FragmentsObserver.FetchRedirectLimit);

        // A browser stops past the Fetch standard's limit with a network error, so the navigation lands nowhere.
        return cut >= 0 ? new Navigation(hops[..cut], null, prompt) : new Navigation(hops, "/", prompt);
    }

    /// <summary>An engine whose pages answer from the shell's N, the managed-login session each page holds, and the probe's chain.</summary>
    private sealed class Scripted(string name, Func<int> n, int limit) : IEngine
    {
        public string Name => name;

        public int Limit => limit;

        public int N => n();

        public string Version => name == "webkit" ? "26.6" : "153.0";

        public List<(Uri Url, bool Fill)> Navigations { get; } = [];

        public Func<JsonElement> Evaluated { get; init; } = () => JsonDocument.Parse("""{ "loaded": true, "chunk": "/fragments/x/assets/panel.js" }""").RootElement;

        public Func<Uri, bool, Navigation?>? Answer { get; init; }

        public Task<IEnginePage> OpenAsync(CancellationToken ct) => Task.FromResult<IEnginePage>(new Page(this));

        private sealed class Page(Scripted engine) : IEnginePage
        {
            private bool providerSession;

            public Task<Navigation> NavigateAsync(Uri url, bool fill, string landed, CancellationToken ct)
            {
                engine.Navigations.Add((url, fill));
                if (engine.Answer?.Invoke(url, fill) is { } answered)
                {
                    return Task.FromResult(answered);
                }

                if (url.Host == "127.0.0.1")
                {
                    int length = int.Parse(url.AbsolutePath[3..], System.Globalization.CultureInfo.InvariantCulture);
                    return Task.FromResult(length <= engine.Limit ? new Navigation([], RedirectChain.End, false) : new Navigation([], null, false));
                }

                if (!providerSession && !fill)
                {
                    return Task.FromResult(new Navigation([App(302, "/bff/login"), Provider(200, "/login")], null, true));
                }

                Navigation signIn = SignIn(engine.N, !providerSession);
                providerSession = true;
                return Task.FromResult(signIn);
            }

            public Task<JsonElement> EvaluateAsync(string script, object? argument, CancellationToken ct) => Task.FromResult(engine.Evaluated());

            public Task ForgetGatewayAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Engines(IReadOnlyList<Scripted> launched, IReadOnlyList<string> refused) : IEngines
    {
        public Task<(IReadOnlyList<IEngine> Launched, IReadOnlyList<string> Refused)> LaunchAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<IEngine>, IReadOnlyList<string>)>(([.. launched.Where(engine => names.Contains(engine.Name))], [.. refused.Where(reason => names.Any(name => reason.StartsWith(name, StringComparison.Ordinal)))]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void A_navigations_runs_split_at_each_page_and_its_hops_are_the_steps_that_go_to_the_provider()
    {
        // Mutation: counting the chain's last step, which redirects to returnUrl, as a hop (HopStarts without its provider
        // check) records three hops at N = 2 and fails this test.
        Navigation interactive = SignIn(2, prompt: true);

        Assert.Equal([3, 9], interactive.Runs.Select(run => run.Count));
        Assert.All(interactive.Runs.SelectMany(run => run), redirect => Assert.DoesNotContain('?', redirect.Path));
        IReadOnlyList<IReadOnlyList<IReadOnlyList<Redirect>>> hops = FragmentsNavigations.ChainHops(SignIn(2, prompt: false));
        Assert.Equal(2, hops.Count);
        Assert.All(hops, hop => Assert.Equal(3, Assert.Single(hop).Count));
        Assert.Equal([new ChainStep("cross-site", "navigate", "document"), new ChainStep("cross-site", "navigate", "document")], FragmentsNavigations.ChainSteps(SignIn(2, prompt: false)));
        Assert.Empty(FragmentsNavigations.ChainHops(SignIn(0, prompt: false)));
    }

    [Fact]
    public async Task O13s_chain_runs_in_each_engine_that_launched_and_names_the_one_that_did_not()
    {
        // Mutation: recording Completed as true whatever the navigation landed on (FragmentsBrowserRows.ChainAsync) passes
        // the stopped run below and fails the last assertion.
        Scripted chromium = new("chromium", () => 2, 19);
        Scripted webkit = new("webkit", () => 2, 20);
        FragmentsBrowserRows rows = new(new Engines([chromium, webkit], ["firefox: Failed to launch the browser process."]), Shell);

        (ChainRecord record, IReadOnlyList<string> agents, IReadOnlyList<string> refused) = await rows.ChainAsync(Ct);

        Assert.Equal(["chromium 153.0", "webkit 26.6"], agents);
        Assert.Equal(["firefox: Failed to launch the browser process."], refused);
        RowResult result = FragmentsObserver.Chain(record, new FragmentsContext(2, agents, []));
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.Equal([(Shell.Url("/bff/login?returnUrl=%2F"), true)], chromium.Navigations);

        Scripted stopped = new("chromium", () => 2, 19) { Answer = (_, _) => SignIn(2, prompt: true) with { Landed = null } };
        (ChainRecord failed, _, _) = await new FragmentsBrowserRows(new Engines([stopped], []), Shell).ChainAsync(Ct);
        Assert.Equal(Verdict.Failed, FragmentsObserver.Chain(failed, new FragmentsContext(2, ["chromium 153.0"], [])).Verdict);
    }

    [Fact]
    public async Task O12_imports_a_fragment_at_entry_js_and_one_at_another_name_after_signing_in()
    {
        // Mutation: choosing both cases from fragments at entry.js (FragmentsBrowserRows.EntryImportsAsync) leaves no other
        // entry name and fails the verdict.
        LiveFragment[] fragments = [new("orders", "f1", "entry.js", ["orders"]), new("reports", "f1", "main.js", ["reports"])];
        int asked = 0;
        Scripted answering = new("chromium", () => 2, 19)
        {
            Evaluated = () => JsonDocument.Parse($$"""{ "loaded": true, "chunk": "/fragments/{{fragments[asked++].FragmentId}}/assets/panel.d2ff9911.js" }""").RootElement,
        };

        (EntryImportRecord record, IReadOnlyList<string> agents) = await new FragmentsBrowserRows(new Engines([answering], []), Shell).EntryImportsAsync(fragments, Ct);

        Assert.Equal(["chromium 153.0"], agents);
        Assert.Equal([("orders", "entry.js"), ("reports", "main.js")], record.Cases.Select(each => (each.FragmentId, each.Entry)));
        RowResult result = FragmentsObserver.EntryImports(record);
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.Contains("import(root + entry)", FragmentsBrowserRows.ImportScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O8_opens_the_fallback_inside_the_hour_and_again_once_it_has_passed()
    {
        // Mutation: dropping the wait for the managed-login hour from FragmentsBrowserRows.FallbackAsync sends both halves
        // inside the hour and fails the verdict.
        ManualClock clock = new(FragmentsObserverTests.SignedIn);
        bool expired = false;
        Scripted chromium = new("chromium", () => 2, 19)
        {
            Answer = (url, fill) => url.Query.Contains("fragment=", StringComparison.Ordinal)
                ? expired ? new Navigation([App(302, "/bff/login"), Provider(200, "/login")], null, true) : new Navigation([App(302, "/bff/login"), App(200, "/")], "/", false)
                : null,
        };
        FragmentsBrowserRows rows = new(new Engines([chromium], []), Shell)
        {
            Clock = clock,
            Delay = (by, _) =>
            {
                clock.Advance(by);
                expired = clock.GetUtcNow() - FragmentsObserverTests.SignedIn >= FragmentsBounds.ManagedLoginHour;
                return Task.CompletedTask;
            },
        };

        (FallbackRecord record, _) = await rows.FallbackAsync(new LiveFragment("orders", "f1", "entry.js", []), Ct);

        RowResult result = FragmentsObserver.Fallback(record);
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.Equal([true, false, false], chromium.Navigations.Select(each => each.Fill));
        Assert.Equal((false, false), (record.Inside!.ShowedPage, record.Inside.Prompted));
        Assert.Equal((true, true), (record.After!.ShowedPage, record.After.Prompted));
        Assert.Equal(Shell.Url("/bff/login?fragment=orders&returnUrl=%2F"), chromium.Navigations[^1].Url);
    }

    private sealed class Spactl
    {
        public List<string[]> Commands { get; } = [];

        public List<int> Fragments { get; } = [];

        public Func<int, int?> Exit { get; init; } = _ => 0;

        public int Live { get; private set; } = 6;

        public async Task<int?> RunAsync(IReadOnlyList<string> arguments, IObjectStore store, CancellationToken ct)
        {
            Commands.Add([.. arguments]);
            JsonObject definition = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(arguments[2], ct))!;
            int n = (definition["fragments"] as JsonArray)?.Count ?? 0;
            Fragments.Add(n);
            int? exit = Exit(n);
            if (exit == 0)
            {
                Live = n;
            }

            return exit;
        }
    }

    /// <summary>The releases the live entry gave each of the fixture shell's six fragments.</summary>
    private static readonly IReadOnlyDictionary<string, string> Releases =
        Definition()["fragments"]!.AsArray().ToDictionary(fragment => fragment!["fragmentId"]!.GetValue<string>(), fragment => $"{fragment!["fragmentId"]!.GetValue<string>()}-r1", StringComparer.Ordinal);

    private static JsonObject Definition() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Gateway.TestHarness.FixtureFiles.RepositoryRoot, "fixtures", "fragments", "shell.json")))!;

    [Fact]
    public async Task O1_writes_each_N_runs_every_engine_at_it_probes_each_and_puts_the_shell_back()
    {
        // Mutation: dropping the restore from FragmentsBrowserRows.RedirectsAsync leaves the shell at N = 6 here, where it
        // started at 2, and fails the second assertion.
        Spactl spactl = new();
        O1Lever lever = new(spactl.RunAsync, new InMemoryObjectStore(), Definition(), Releases, 2, "platform.json", "outputs.json", TimeSpan.Zero);
        Scripted[] engines = [new("chromium", () => spactl.Live, 19), new("firefox", () => spactl.Live, 20), new("webkit", () => spactl.Live, 20)];
        FragmentsBrowserRows rows = new(new Engines(engines, []), Shell) { Delay = (_, _) => Task.CompletedTask };

        RedirectRecord record = await rows.RedirectsAsync(lever, Ct);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 2], spactl.Fragments);
        Assert.Equal(2, spactl.Live);
        Assert.Equal(["app", "add", spactl.Commands[0][2], "--config", "platform.json", "--outputs", "outputs.json"], spactl.Commands[0]);
        string[] ids = [.. Definition()["fragments"]!.AsArray().Select(fragment => fragment!["fragmentId"]!.GetValue<string>())];
        Assert.Equal(["app", "add", spactl.Commands[^1][2], "--fragment-release", $"{ids[0]}={ids[0]}-r1,{ids[1]}={ids[1]}-r1", "--config", "platform.json", "--outputs", "outputs.json"], spactl.Commands[^1]);
        RowResult result = FragmentsObserver.Redirects(record);
        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        Assert.Equal(new BoundaryProbe(19, 20), record.Engines.Single(each => each.Engine == "chromium").Probe);
        Assert.Contains("the cap is 5", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O1_refuses_to_start_when_the_live_entry_names_no_release_for_a_fragment_it_must_write()
    {
        // Mutation: dropping the release check from O1Lever.WriteAsync lets spactl refuse the new fragment mid-row
        // ("is new to the entry"), and fails the message assertion.
        Spactl spactl = new();
        O1Lever lever = new(spactl.RunAsync, new InMemoryObjectStore(), Definition(), new Dictionary<string, string>(StringComparer.Ordinal), 0, "platform.json", "outputs.json", TimeSpan.Zero);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => lever.WriteAsync(1, Ct));

        Assert.Contains("naming every fragment's release", refused.Message, StringComparison.Ordinal);
        Assert.Empty(spactl.Commands);
    }

    [Fact]
    public async Task O1_puts_the_shell_back_when_an_N_is_refused_and_says_the_policy_must_be_raised()
    {
        // Mutation: Restoring.RunAsync rethrowing before its restore leaves the shell at the last N written and fails this test.
        Spactl spactl = new() { Exit = n => n == 6 ? 1 : 0 };
        O1Lever lever = new(spactl.RunAsync, new InMemoryObjectStore(), Definition(), Releases, 2, "platform.json", "outputs.json", TimeSpan.Zero);
        FragmentsBrowserRows rows = new(new Engines([new("chromium", () => spactl.Live, 19)], []), Shell) { Delay = (_, _) => Task.CompletedTask };

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rows.RedirectsAsync(lever, Ct));

        Assert.Contains("Policy:FragmentsPerShell", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, spactl.Fragments[^1]);
        Assert.Equal(2, spactl.Live);
    }

    [Fact]
    public async Task O1_with_an_engine_that_did_not_launch_is_not_proved_naming_it()
    {
        // Mutation: filling a missing engine's runs from another engine's in FragmentsBrowserRows.RedirectsAsync passes this record.
        Spactl spactl = new();
        O1Lever lever = new(spactl.RunAsync, new InMemoryObjectStore(), Definition(), Releases, 6, "platform.json", "outputs.json", TimeSpan.Zero);
        FragmentsBrowserRows rows = new(new Engines([new("chromium", () => spactl.Live, 19), new("webkit", () => spactl.Live, 20)], ["firefox: Failed to launch the browser process."]), Shell)
        {
            Delay = (_, _) => Task.CompletedTask,
        };

        RowResult result = FragmentsObserver.Redirects(await rows.RedirectsAsync(lever, Ct));

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("firefox has no run", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_redirect_chain_answers_one_shorter_until_its_end()
    {
        // Mutation: answering /r/1 with a page in RedirectChain.Answer shortens every chain by one and fails this test.
        Assert.StartsWith("HTTP/1.1 302 Found\r\nLocation: /r/0\r\n", RedirectChain.Answer("/r/1"), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 302 Found\r\nLocation: /r/19\r\n", RedirectChain.Answer("/r/20"), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200 OK", RedirectChain.Answer("/r/0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_routing_proxy_sends_the_cells_hosts_to_the_tunnel_and_names_no_other()
    {
        // Mutation: routing every host to the tunnel in RoutingProxy.Route fails the second assertion.
        Assert.Equal(("127.0.0.1", 8443), RoutingProxy.Route("shell.spa-poc.test", 443, "spa-poc.test", 8443));
        Assert.Equal(("cognito.example.com", 443), RoutingProxy.Route("cognito.example.com", 443, "spa-poc.test", 8443));
        Assert.Equal(("evilspa-poc.test", 443), RoutingProxy.Route("evilspa-poc.test", 443, "spa-poc.test", 8443));

        TcpListener tunnel = new(IPAddress.Loopback, 0);
        tunnel.Start();
        try
        {
            using RoutingProxy proxy = RoutingProxy.Start("spa-poc.test", ((IPEndPoint)tunnel.LocalEndpoint).Port);
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, Ct);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync("CONNECT shell.spa-poc.test:443 HTTP/1.1\r\nHost: shell.spa-poc.test:443\r\n\r\n"u8.ToArray(), Ct);
            using TcpClient reached = await tunnel.AcceptTcpClientAsync(Ct);
            byte[] answer = new byte[64];
            int read = await stream.ReadAsync(answer, Ct);
            Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(answer, 0, read), StringComparison.Ordinal);
            await stream.WriteAsync("hello"u8.ToArray(), Ct);
            byte[] carried = new byte[5];
            await reached.GetStream().ReadExactlyAsync(carried, Ct);
            Assert.Equal("hello", Encoding.ASCII.GetString(carried));
        }
        finally
        {
            tunnel.Stop();
        }
    }
}
