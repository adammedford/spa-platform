using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The stack a browser test runs against in a sitting (#209): the deployed cell, through the tunnel,
/// against Cognito. Chosen by <c>SPA_E2E_STACK=deployed</c> (<see cref="StackSwitch"/>).
///
/// <para>A sitting has, before the run: the cell deployed and seeded, <c>tools/scripts/tunnel.sh</c>
/// up, the local authority <c>tools/scripts/certs.sh</c> made trusted on the workstation, and the
/// operator's own AWS session in the environment (the <c>spa-platform</c> profile, with
/// <c>tools/aws-project.env</c> sourced). The release switch of F-06 also needs
/// <c>SPA_E2E_PLATFORM_CONFIG</c>, the platform configuration file <c>spactl release activate</c>
/// bounds the registry with, and <c>spactl</c> able to sign in (#206).</para>
///
/// <para>Every name of the cell comes from the infrastructure outputs document (<see cref="Cell"/>),
/// <c>infra/envs/poc/outputs.json</c> unless <c>SPA_E2E_OUTPUTS</c> names another, and never from
/// Terraform state. The tunnel's port is <c>SPA_TUNNEL_PORT</c>, else 8443, the default
/// <c>tunnel.sh</c> forwards and every registry host carries.</para>
///
/// <para>One cell serves every test, so one deployed stack runs at a time in a process: the gateway
/// log and the provider's session are the cell's, and a row that counts a log line must see only its
/// own test's traffic. A stack reads the log from the moment it started.</para>
/// </summary>
public sealed class DeployedStack : IBrowserStack
{
    /// <summary>The variable naming the outputs document, when it is not the repository's.</summary>
    public const string OutputsVariable = "SPA_E2E_OUTPUTS";

    /// <summary>The tunnel's local port, the same variable <c>tools/scripts/tunnel.sh</c> reads.</summary>
    public const string TunnelPortVariable = "SPA_TUNNEL_PORT";

    /// <summary>The platform configuration file for <c>spactl release activate --config</c>.</summary>
    public const string PlatformConfigVariable = "SPA_E2E_PLATFORM_CONFIG";

    /// <summary>The test user every profile signs in as (fixtures/cognito/pool.json).</summary>
    public const string User = "alice";

    /// <summary>
    /// The apps root's access-token life, five minutes (infra/envs/poc/apps/main.tf,
    /// <c>access_token_validity</c> in minutes). A test reads that file to hold this to it.
    /// </summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a log line may take to reach CloudWatch and be returned by a filter.</summary>
    private static readonly TimeSpan LogArrival = TimeSpan.FromSeconds(90);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly IBrowser browser;
    private readonly CellAws aws;
    private readonly string outputsPath;
    private readonly DateTimeOffset started;
    private readonly List<Task<Exchange?>> exchanges = [];
    private Password? password;

    private DeployedStack(Cell cell, CellAws aws, IBrowser browser, string outputsPath, DateTimeOffset started)
    {
        Cell = cell;
        this.aws = aws;
        this.browser = browser;
        this.outputsPath = outputsPath;
        this.started = started;
    }

    public StackKind Kind => StackKind.Deployed;

    /// <summary>The cell's names, from the outputs document.</summary>
    public Cell Cell { get; }

    /// <summary>The cell's AWS reads: a password, the gateway's log.</summary>
    public CellAws Aws => aws;

    /// <summary>When this stack started; its log reads begin here.</summary>
    public DateTimeOffset Started => started;

    public string ProviderHost => Cell.ProviderHost;

    public string AuthorizeEndpoint => Cell.AuthorizeEndpoint;

    public string EndSessionEndpoint => Cell.EndSessionEndpoint;

    public TimeSpan RegistryPropagation => TimeSpan.FromMinutes(2);

    public static async Task<DeployedStack> StartAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string outputsPath = Environment.GetEnvironmentVariable(OutputsVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "outputs.json");
        if (!File.Exists(outputsPath))
        {
            throw new InvalidOperationException(
                $"the deployed stack reads the cell from {outputsPath}, and there is none: seed.sh and deploy.sh write it after every apply, or {OutputsVariable} names another.");
        }

        Cell cell = Cell.Parse(await File.ReadAllTextAsync(outputsPath, ct));
        int port = TunnelPort();
        await TunnelIsUpAsync(port, ct);

        await OneAtATime.WaitAsync(ct);
        try
        {
            // Every app host goes to the tunnel, whatever the hosts file says; the provider is reached
            // on the internet as it is. The context trusts only what the workstation trusts, so the
            // ALB's certificate must chain to the authority certs.sh installed.
            IBrowser browser = await Chromium.LaunchAsync("--host-resolver-rules=" + cell.ResolverRule(port));

            // A few seconds back, so a workstation clock a little ahead of AWS's loses no line.
            return new DeployedStack(cell, new CellAws(cell, new ProcessCommandRunner()), browser, outputsPath, DateTimeOffset.UtcNow.AddSeconds(-5));
        }
        catch
        {
            OneAtATime.Release();
            throw;
        }
    }

    public HarnessApp App(string appId) => Cell.App(appId);

    public async Task<Profile> OpenAsync()
    {
        // No IgnoreHTTPSErrors: Chromium must trust the cell's certificate through the local authority.
        IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = false });

        // There is no stand-in load balancer to keep the exchanges, so the browser's own responses are
        // kept as they arrive. A listener only observes: the requests are the browser's own, with the
        // Fetch Metadata a route would have lost (TlsLoadBalancer).
        context.Response += (_, response) =>
        {
            lock (exchanges)
            {
                exchanges.Add(RecordAsync(response));
            }
        };
        IPage page = await context.NewPageAsync();
        return new Profile(this, context, page);
    }

    public async Task<IReadOnlyList<Exchange>> ExchangesAsync()
    {
        Task<Exchange?>[] pending;
        lock (exchanges)
        {
            pending = [.. exchanges];
        }

        return [.. (await Task.WhenAll(pending)).OfType<Exchange>()];
    }

    /// <summary>
    /// Cognito's managed login, filled as a person fills it: the user name, then the password if the
    /// page asks for it on a second step. When Cognito still holds a session for this browser it sends
    /// the browser straight back to the callback and there is no form.
    /// </summary>
    public async Task CompleteLoginAsync(Profile profile, HarnessApp app)
    {
        IPage page = profile.Page;
        string shell = app.Url("/").ToString();
        await page.WaitForURLAsync(url => url == shell || new Uri(url).Host == ProviderHost);
        if (new Uri(page.Url).Host != ProviderHost)
        {
            return;
        }

        password ??= await aws.PasswordAsync(User, TestContext.Current.CancellationToken);

        // Managed login's fields carry the names Cognito's hosted pages always used, username and
        // password (https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-managed-login.html).
        ILocator username = page.Locator("input[name='username']:visible").First;
        ILocator secret = page.Locator("input[name='password']:visible").First;
        ILocator submit = page.Locator("button[type='submit']:visible").First;

        await username.FillAsync(User);
        if (!await secret.IsVisibleAsync())
        {
            await submit.ClickAsync();
            await secret.WaitForAsync();
        }

        // The one place the password goes. Nothing here traces, screenshots or prints the page.
        await secret.FillAsync(password.Reveal());
        await submit.ClickAsync();
    }

    public async Task<IReadOnlyList<JsonElement>> LogLinesAsync(string message) =>
        [.. (await aws.GatewayLinesAsync(started, TestContext.Current.CancellationToken)).Where(line => MessageStarts(line, message))];

    public async Task<IReadOnlyList<JsonElement>> SettledLogLinesAsync(string message)
    {
        await aws.WaitForGatewayLineAsync(started, line => MessageStarts(line, message), LogArrival, $"a line whose msg starts \"{message}\"", TestContext.Current.CancellationToken);

        // Lines written together reach CloudWatch in one batch from the log driver, but a second line
        // a moment later may come in the next one.
        await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        return await LogLinesAsync(message);
    }

    /// <summary>
    /// The request lines since <paramref name="since"/>; waits for one that carries
    /// <paramref name="rule"/> (for <paramref name="appId"/>, when given), and fails if none comes.
    /// The rule id is in the log line and nowhere a browser can see (contracts/request-gate.md -> "Rule
    /// ids"), so the rows that assert one read it from CloudWatch here.
    /// </summary>
    public Task<JsonElement> AssertRuleLoggedAsync(DateTimeOffset since, string rule, string? appId = null) =>
        aws.WaitForGatewayLineAsync(
            since,
            line => line.TryGetProperty("msg", out JsonElement msg) && msg.GetString() == "request"
                && line.TryGetProperty("rule", out JsonElement value) && value.GetString() == rule
                && (appId is null || (line.TryGetProperty("app_id", out JsonElement app) && app.GetString() == appId)),
            LogArrival,
            $"a request line with rule {rule}{(appId is null ? "" : " for " + appId)}",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The access token Cognito issues lives <see cref="AccessTokenLifetime"/>, and nothing can issue
    /// one already expired, so the row waits it out: the next call is past expiry.
    /// </summary>
    public async Task SignInWithExpiredAccessTokenAsync(Profile profile, HarnessApp app)
    {
        await profile.SignInAsync(app);
        await Task.Delay(AccessTokenLifetime + TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>Cognito signs the user out at its logout endpoint, so the next authorization shows its login form, and the browser stays there.</summary>
    public async Task StopAtProviderLoginAsync(Profile profile, Func<Task> action)
    {
        await action();
        await profile.Page.WaitForURLAsync(url => new Uri(url).Host == ProviderHost && new Uri(url).AbsolutePath != "/logout");
        await Assertions.Expect(profile.Page.Locator("input[name='username']:visible").First).ToBeVisibleAsync();
    }

    /// <summary>The release the live registry gives the app, read from the bucket (<see cref="CellAws.ReleaseOfAsync"/>).</summary>
    public Task<string> ReleaseOfAsync(string appId) => aws.ReleaseOfAsync(appId, TestContext.Current.CancellationToken);

    /// <summary>
    /// <c>spactl release publish</c> from a directory of the files. A release prefix is written once
    /// (ADR-0009) and spactl leaves identical bytes alone, so a second sitting publishing the same
    /// files under the same name is a no-op.
    /// </summary>
    public async Task PublishReleaseAsync(string appId, string release, IReadOnlyDictionary<string, byte[]> files)
    {
        string directory = Directory.CreateTempSubdirectory("spa-e2e-release-").FullName;
        try
        {
            foreach ((string key, byte[] bytes) in files)
            {
                string path = Path.Combine(directory, key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            }

            await SpactlAsync(["release", "publish", appId, directory, release, "--outputs", outputsPath]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public async Task SwitchReleaseAsync(HarnessApp app, string release)
    {
        string config = Environment.GetEnvironmentVariable(PlatformConfigVariable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException(
                $"switching a release on the cell runs spactl release activate, which needs {PlatformConfigVariable}: the platform configuration the gateway runs with, as seed.sh reads it from the task definition.");
        await SpactlAsync(["release", "activate", app.AppId, release, "--config", config, "--outputs", outputsPath]);
    }

    /// <summary><c>SPACTL</c> when it names an executable, else <c>dotnet run --project tools/spactl</c>, as <c>tools/scripts/seed.sh</c> runs it.</summary>
    private static Task<string> SpactlAsync(string[] arguments)
    {
        ProcessCommandRunner runner = new();
        return Environment.GetEnvironmentVariable("SPACTL") is { Length: > 0 } spactl
            ? runner.RunAsync(spactl, arguments, TestContext.Current.CancellationToken)
            : runner.RunAsync("dotnet", ["run", "--project", Path.Combine(FixtureFiles.RepositoryRoot, "tools", "spactl"), "--", .. arguments], TestContext.Current.CancellationToken);
    }

    private async Task<Exchange?> RecordAsync(IResponse response)
    {
        Uri url = new(response.Url);
        if (!url.Host.EndsWith("." + Cell.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return null; // The provider's own pages are not the cell's exchanges.
        }

        try
        {
            IReadOnlyList<Header> received = await response.HeadersArrayAsync();
            IReadOnlyList<Header> sent = await response.Request.HeadersArrayAsync();
            byte[] body;
            try
            {
                body = await response.BodyAsync();
            }
            catch (PlaywrightException)
            {
                body = []; // A redirect's body is not kept by the browser.
            }

            return new Exchange(
                response.Request.Method,
                url,
                sent.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(header => header.Value)), StringComparer.OrdinalIgnoreCase),
                response.Status,
                [.. received.Select(header => KeyValuePair.Create(header.Name, header.Value))],
                body);
        }
        catch (PlaywrightException)
        {
            return null; // The page closed before the response could be read.
        }
    }

    private static bool MessageStarts(JsonElement line, string message) =>
        line.TryGetProperty("msg", out JsonElement msg) && msg.GetString()?.StartsWith(message, StringComparison.Ordinal) == true;

    private static int TunnelPort() =>
        Environment.GetEnvironmentVariable(TunnelPortVariable) is { Length: > 0 } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536
                ? port
                : throw new InvalidOperationException($"{TunnelPortVariable} is '{text}', not a port.")
            : 8443;

    /// <summary>Fails, naming the script that starts it, when nothing listens on the tunnel's port: a run without the tunnel is not a deployed run.</summary>
    private static async Task TunnelIsUpAsync(int port, CancellationToken ct)
    {
        using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
        }
        catch (Exception refused) when (refused is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"nothing listens on 127.0.0.1:{port}: start tools/scripts/tunnel.sh{(port == 8443 ? "" : " --local-port " + port)} first.", refused);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await browser.DisposeAsync();
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
