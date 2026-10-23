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
/// bounds the registry with, and <c>spactl</c> able to sign in (#206). F-07's onboarding (#212) needs
/// both too, and the apps root initialised as its README gives it, with Terraform on the path.</para>
///
/// <para>Every name of the cell comes from the infrastructure outputs document (<see cref="Cell"/>),
/// <c>infra/envs/poc/outputs.json</c> unless <c>SPA_E2E_OUTPUTS</c> names another, and never from
/// Terraform state. The tunnel's port is <c>SPA_TUNNEL_PORT</c>, else 8443, the default
/// <c>tunnel.sh</c> forwards and every registry host carries.</para>
///
/// <para>What the stand-in load balancer does locally the browser does here (#211): a login is held at
/// its callback inside the browser (<see cref="NavigationHold"/>), a foreign page comes from this run's
/// own listener on loopback (<see cref="ForeignSites"/>), and the exchanges are the browser's own
/// record (<see cref="BrowserExchanges"/>). The raw client goes through the tunnel
/// (<see cref="RawClient"/>).</para>
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

    /// <summary>The test user a profile signs in as unless a row names another (fixtures/cognito/pool.json).</summary>
    public const string User = "alice";

    /// <summary>The other test user, for a row that needs a second person: another user's token (N-06), an attacker's login (N-04, N-26).</summary>
    public const string OtherUser = "bob";

    /// <summary>
    /// The apps root's access-token life, five minutes (infra/envs/poc/apps/main.tf,
    /// <c>access_token_validity</c> in minutes). A test reads that file to hold this to it.
    /// </summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long a log line may take to reach CloudWatch and be returned by a filter.</summary>
    private static readonly TimeSpan LogArrival = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How far the workstation's clock may be from AWS's, either way. A log mark reaches this far back,
    /// so a clock up to this far ahead loses none of its own test's lines; <see cref="Quiet"/> covers a
    /// clock up to this far behind.
    /// </summary>
    internal static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after the last stack on the cell ended a new one starts (<see cref="StartDelay"/>). A mark
    /// reaches <see cref="ClockMargin"/> back of a workstation clock that may itself be that far behind
    /// AWS's. Twice the margin and two seconds puts before this stack's start and every mark it makes
    /// each request line with a rule that the last stack caused, which is all
    /// <see cref="AssertRuleLoggedAsync"/>, the one read after a mark, matches: a rule line is a refusal,
    /// written as the pipeline returns from the stage that ended the request (contracts/telemetry.md), so a
    /// moment after the request that caused it. It does not hold for every line the last stack caused: a
    /// request the browser left in flight can end, and its line be written, later than that.
    /// </summary>
    internal static readonly TimeSpan Quiet = (2 * ClockMargin) + TimeSpan.FromSeconds(2);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>When the last stack on the cell ended, on the workstation's clock; written and read under <see cref="OneAtATime"/>, which orders the two.</summary>
    private static DateTimeOffset? lastEnded;

    private readonly IBrowser browser;
    private readonly CellAws aws;
    private readonly CellRuleLog rules;
    private readonly ForeignSites foreign;
    private readonly string outputsPath;
    private readonly int tunnelPort;
    private readonly DateTimeOffset started;
    private readonly BrowserExchanges exchanges;
    private readonly Dictionary<string, Password> passwords = [];
    private readonly System.Net.Security.RemoteCertificateValidationCallback? rawValidation;
    private string user = User;
    private HttpClient? raw;

    private DeployedStack(Cell cell, CellAws aws, IBrowser browser, ForeignSites foreign, string outputsPath, int tunnelPort, DateTimeOffset started,
        System.Net.Security.RemoteCertificateValidationCallback? rawValidation, TimeSpan ruleArrival)
    {
        Cell = cell;
        this.aws = aws;
        rules = new CellRuleLog(aws, ruleArrival);
        this.rawValidation = rawValidation;
        this.browser = browser;
        this.foreign = foreign;
        this.outputsPath = outputsPath;
        this.tunnelPort = tunnelPort;
        this.started = started;
        exchanges = new BrowserExchanges(cell.Domain);
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

    /// <summary>
    /// The cell's wait for a switched release: two minutes. The gateway's next poll comes within one
    /// interval, which the jitter only shortens (<c>RegistryPoller.NextDelay</c>), and the cell runs the
    /// module's <c>registry_poll_seconds</c> default; a test holds this to twice that default.
    /// </summary>
    internal static readonly TimeSpan CellRegistryPropagation = TimeSpan.FromMinutes(2);

    public TimeSpan RegistryPropagation => CellRegistryPropagation;

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
        return await StartAsync(new DeployedStackSetup(cell, outputsPath, new ProcessCommandRunner(), TunnelPort()));
    }

    /// <summary>
    /// The stack over a given cell, tunnel port and command runner: what <see cref="StartAsync()"/> builds
    /// from the environment, and what a test builds over a loopback listener standing in for the tunnel
    /// and a recorded runner standing in for AWS (#211).
    /// </summary>
    internal static async Task<DeployedStack> StartAsync(DeployedStackSetup setup)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Cell cell = setup.Cell;
        int port = setup.TunnelPort;
        await TunnelIsUpAsync(port, ct);

        await OneAtATime.WaitAsync(ct);
        ForeignSites? foreign = null;
        try
        {
            await Task.Delay(StartDelay(DateTimeOffset.UtcNow, lastEnded, setup.Quiet ?? Quiet), ct);

            // Every app host goes to the tunnel, whatever the hosts file says, except the sibling, which
            // goes with the site elsewhere to this run's own foreign listener; the provider is reached on
            // the internet as it is. The context trusts only what the workstation trusts, and the foreign
            // listener's key, so the ALB's certificate must chain to the authority certs.sh installed.
            foreign = await ForeignSites.StartAsync(cell.Domain, ct);
            IBrowser browser = await Chromium.LaunchAsync(
                $"--host-resolver-rules={foreign.ResolverRules}, {cell.ResolverRule(port)}",
                ForeignSites.TrustSwitch([foreign.Spki, .. setup.TrustedKeys]));

            return new DeployedStack(cell, new CellAws(cell, setup.Runner), browser, foreign, setup.OutputsPath, port, DateTimeOffset.UtcNow - ClockMargin, setup.RawValidation, setup.RuleArrival ?? LogArrival);
        }
        catch
        {
            if (foreign is not null)
            {
                await foreign.DisposeAsync();
            }

            OneAtATime.Release();
            throw;
        }
    }

    /// <summary>
    /// How long a new stack waits before it starts: what is left of <paramref name="quiet"/> since the
    /// last stack ended. Nothing for the first stack in the process, and never more than
    /// <paramref name="quiet"/>, so a workstation clock set back during a run costs one quiet at most.
    /// </summary>
    internal static TimeSpan StartDelay(DateTimeOffset now, DateTimeOffset? previousEnded, TimeSpan quiet) =>
        previousEnded is { } ended
            ? TimeSpan.FromTicks(Math.Clamp((ended + quiet - now).Ticks, 0, quiet.Ticks))
            : TimeSpan.Zero;

    public HarnessApp App(string appId) => Cell.App(appId);

    public async Task<Profile> OpenAsync()
    {
        // No IgnoreHTTPSErrors: Chromium must trust the cell's certificate through the local authority.
        IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = false });

        // There is no stand-in load balancer to keep the exchanges, so the browser keeps them (BrowserExchanges).
        IPage page = await context.NewPageAsync();
        await exchanges.AttachAsync(page);
        return new Profile(this, context, page);
    }

    public Task<IReadOnlyList<Exchange>> ExchangesAsync() => exchanges.AllAsync();

    /// <summary>
    /// Cognito's managed login, filled as a person fills it: the user name, then the password if the
    /// page asks for it on a second step. When Cognito still holds a session for this browser it sends
    /// the browser straight back to the callback and there is no form.
    /// The whole login runs through <see cref="Redacted.PlaywrightAsync(Func{Task})"/>, not only its first
    /// wait: F-07 calls it on the stack directly, and the form's submit is what sends the browser to the
    /// callback with its code (#253).
    /// </summary>
    public Task CompleteLoginAsync(Profile profile, HarnessApp app)
    {
        string shell = app.Url("/").ToString();
        return Redacted.PlaywrightAsync(() => FillLoginFormIfShownAsync(profile, url => url == shell));
    }

    /// <summary>
    /// Waits until the page is at the provider or where the login ends without it
    /// (<paramref name="arrived"/>), and at the provider fills the managed login form as
    /// <see cref="SignInAs"/> last named.
    /// </summary>
    private async Task FillLoginFormIfShownAsync(Profile profile, Func<string, bool> arrived)
    {
        IPage page = profile.Page;
        await Redacted.PlaywrightAsync(() => page.WaitForURLAsync(url => arrived(url) || new Uri(url).Host == ProviderHost));
        if (new Uri(page.Url).Host != ProviderHost)
        {
            return;
        }

        if (!passwords.TryGetValue(user, out Password? password))
        {
            password = await aws.PasswordAsync(user, TestContext.Current.CancellationToken);
            passwords[user] = password;
        }

        // Managed login's fields carry the names Cognito's hosted pages always used, username and
        // password (https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-managed-login.html).
        ILocator username = page.Locator("input[name='username']:visible").First;
        ILocator secret = page.Locator("input[name='password']:visible").First;
        ILocator submit = page.Locator("button[type='submit']:visible").First;

        await username.FillAsync(user);
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

    public string ParentDomain => Cell.Domain;

    public void SignInAs(string user) => this.user = user;

    public LogMark MarkLog() => new(0, DateTimeOffset.UtcNow - ClockMargin);

    /// <summary>
    /// Read from CloudWatch (<see cref="CellRuleLog"/>): the request lines since the mark, one of which
    /// carries the rule and no earlier assertion has matched. A rule line an earlier test caused,
    /// asserted or not, falls before this stack's marks (<see cref="Quiet"/>).
    /// </summary>
    public Task AssertRuleLoggedAsync(LogMark after, string rule, string? appId = null) =>
        rules.AssertLoggedAsync(after.At, rule, appId, TestContext.Current.CancellationToken);

    /// <summary>Through the tunnel, validating the ALB's certificate as the workstation does (<see cref="RawClient"/>).</summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
    {
        raw ??= RawClient.To($"127.0.0.1:{tunnelPort}", rawValidation);
        return raw.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Held in the browser (<see cref="NavigationHold"/>), since nothing between the browser and the
    /// ALB is the test's. Cognito shows its form unless it already knows this browser, and then sends
    /// it straight to the callback.
    /// </summary>
    public Task<Uri> StopAtCallbackAsync(Profile profile, HarnessApp app, Func<Task> start)
    {
        string callback = app.Url("/signin-oidc").ToString();
        return NavigationHold.HoldAsync(profile.Page, callback, async () =>
        {
            await start();
            await FillLoginFormIfShownAsync(profile, url => url.StartsWith(callback, StringComparison.Ordinal));
        });
    }

    /// <summary>Served by this run's foreign listener, on loopback like the tunnel (<see cref="ForeignSites"/>).</summary>
    public async Task OpenForeignPageAsync(Profile profile, Uri url, string html)
    {
        foreign.Serve(url, html);
        await profile.GotoAsync(url.ToString());
    }

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
        await Redacted.PlaywrightAsync(async () =>
        {
            await action();
            await profile.Page.WaitForURLAsync(url => new Uri(url).Host == ProviderHost && new Uri(url).AbsolutePath != "/logout");
            await Assertions.Expect(profile.Page.Locator("input[name='username']:visible").First).ToBeVisibleAsync();
        });
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

    public async Task SwitchReleaseAsync(HarnessApp app, string release, CancellationToken ct) =>
        await SpactlAsync(["release", "activate", app.AppId, release, "--config", PlatformConfig("switching a release on the cell runs spactl release activate"), "--outputs", outputsPath], ct);

    /// <summary>
    /// The onboarding the plan gives for a third SPA (<see cref="CellOnboarding"/>): the apps root's
    /// plan, read and then applied, spactl's publish and add, and <c>spactl wait</c> on the tunnel.
    /// </summary>
    public async Task<HarnessApp> OnboardAsync(string appId)
    {
        LastOnboarding = await Onboarding("onboarding an app runs spactl app add").OnboardAsync(appId, TestContext.Current.CancellationToken);
        return LastOnboarding.App;
    }

    /// <summary>
    /// Takes the app off under a bound of its own rather than the test's token: F-07 takes app 3 off when
    /// its row fails, a cancelled row included, and a clean-up cancelled with the row would leave app 3's
    /// client, branding and secret live on the cell. The bound covers spactl's 300-second wait and the
    /// apps root's plan and apply.
    /// </summary>
    public async Task OffboardAsync(string appId)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromMinutes(15));
        await Onboarding("taking an onboarded app off runs spactl app remove").OffboardAsync(appId, cleanup.Token);
    }

    /// <summary>The last onboarding on this stack: the app, the plan read before its apply, and the revision that named it.</summary>
    public Onboarded? LastOnboarding { get; private set; }

    /// <summary>
    /// <c>spactl wait</c> reads <c>/readyz</c> on app 1's host through the tunnel: the name resolves to
    /// loopback through the hosts-file line certs.sh asks for, and the port is the tunnel's.
    /// </summary>
    private CellOnboarding Onboarding(string why) => new(Cell, aws, new ProcessCommandRunner(), new OnboardingSetup(
        FixtureFiles.RepositoryRoot,
        Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps"),
        outputsPath,
        PlatformConfig(why),
        new Uri($"https://{App("app1").Host.Split(':')[0]}:{tunnelPort}/"),
        Spactl()));

    private static string PlatformConfig(string why) =>
        Environment.GetEnvironmentVariable(PlatformConfigVariable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException(
                $"{why}, which needs {PlatformConfigVariable}: the platform configuration the gateway runs with, as seed.sh reads it from the task definition.");

    /// <summary><c>SPACTL</c> when it names an executable, else <c>dotnet run --project tools/spactl</c>, as <c>tools/scripts/seed.sh</c> runs it.</summary>
    private static string[] Spactl() =>
        Environment.GetEnvironmentVariable("SPACTL") is { Length: > 0 } spactl
            ? [spactl]
            : ["dotnet", "run", "--project", Path.Combine(FixtureFiles.RepositoryRoot, "tools", "spactl"), "--"];

    private static Task<string> SpactlAsync(string[] arguments) => SpactlAsync(arguments, TestContext.Current.CancellationToken);

    private static Task<string> SpactlAsync(string[] arguments, CancellationToken ct)
    {
        string[] command = Spactl();
        return new ProcessCommandRunner().RunAsync(command[0], [.. command.Skip(1), .. arguments], ct);
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
            raw?.Dispose();
            await browser.DisposeAsync();
            await foreign.DisposeAsync();
        }
        finally
        {
            lastEnded = DateTimeOffset.UtcNow;
            OneAtATime.Release();
        }
    }
}

/// <summary>
/// What a <see cref="DeployedStack"/> runs over. In a sitting: the outputs document's cell, the AWS CLI,
/// the tunnel's port, the workstation's trust store and the full quiet. In a test (#211): a cell whose
/// clients are a local harness's, a recorded runner, a loopback listener's port, that listener's key
/// trusted by the browser and the raw client, and no quiet, since each test's recorded log is its own.
/// </summary>
internal sealed record DeployedStackSetup(Cell Cell, string OutputsPath, ICommandRunner Runner, int TunnelPort)
{
    /// <summary>Certificate keys the browser trusts beyond the workstation's store and the foreign listener's.</summary>
    public IReadOnlyList<string> TrustedKeys { get; init; } = [];

    /// <summary>The raw client's certificate check; the machine's trust store when null.</summary>
    public System.Net.Security.RemoteCertificateValidationCallback? RawValidation { get; init; }

    /// <summary>How long a rule assertion waits for its line; CloudWatch's arrival time when null.</summary>
    public TimeSpan? RuleArrival { get; init; }

    /// <summary>The wait after the last stack ended; <see cref="DeployedStack.Quiet"/> when null.</summary>
    public TimeSpan? Quiet { get; init; }
}
