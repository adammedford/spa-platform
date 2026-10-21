using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Testing;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The stack a browser test runs against, locally: the hermetic cell (task 6.1), which requires a
/// session as ADR-0008 decides because the composition that ships does, behind a
/// <see cref="TlsLoadBalancer"/>, and a Chromium whose resolver sends every app host to that
/// listener, as the tunnel and the hosts file do in the deployed tier
/// (docs/plan/05-infrastructure-and-cost.md). One per test: each starts with an empty provider log,
/// an empty session store and a browser that has seen nothing.
/// </summary>
public sealed class LocalStack : IBrowserStack
{
    private readonly IBrowser browser;

    private LocalStack(GatewayHarness harness, TlsLoadBalancer balancer, IBrowser browser)
    {
        Harness = harness;
        LoadBalancer = balancer;
        this.browser = browser;
    }

    /// <summary>The cell: the gateway, the test provider, the mock API, the bucket and the gateway's log.</summary>
    public GatewayHarness Harness { get; }

    /// <summary>The stand-in load balancer, with every exchange the browser had with the cell.</summary>
    public TlsLoadBalancer LoadBalancer { get; }

    /// <summary>A site that is not under the app domain, for a cross-site page. <c>.example</c> is reserved (RFC 2606).</summary>
    public const string ForeignSite = "attacker.example";

    /// <summary>The parent domain every app host sits under, without the leading dot.</summary>
    public static string Domain => TestApps.HostSuffix.TrimStart('.');

    public static async Task<LocalStack> StartAsync(GatewayHarnessOptions? options = null)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GatewayHarness harness = await GatewayHarness.StartAsync(options, ct);
        TlsLoadBalancer? balancer = null;
        try
        {
            balancer = await TlsLoadBalancer.StartAsync(harness.GatewayAddress, Domain, ct);
            // Every name under the app domain, whatever port its URL names, reaches the load balancer, and
            // so does the foreign site, whose pages it serves. Nothing else is mapped, so the provider on
            // loopback is reached directly, as it is on the internet in the deployed tier.
            IBrowser browser = await Chromium.LaunchAsync(
                $"--host-resolver-rules=MAP *.{Domain} {balancer.Endpoint}, MAP {ForeignSite} {balancer.Endpoint}");
            return new LocalStack(harness, balancer, browser);
        }
        catch
        {
            if (balancer is not null)
            {
                await balancer.DisposeAsync();
            }

            await harness.DisposeAsync();
            throw;
        }
    }

    public StackKind Kind => StackKind.Local;

    public HarnessApp App(string appId) => Harness.App(appId);

    public string ProviderHost => new Uri(Harness.Idp.AuthorizeEndpoint).Host;

    public string AuthorizeEndpoint => Harness.Idp.AuthorizeEndpoint;

    public string EndSessionEndpoint => Harness.Idp.EndSessionEndpoint;

    /// <summary>The test provider approves every authorization at once, so a login needs nothing more from the browser.</summary>
    public Task CompleteLoginAsync(Profile profile, HarnessApp app) => Task.CompletedTask;

    public Task<IReadOnlyList<Exchange>> ExchangesAsync() => Task.FromResult(LoadBalancer.Exchanges);

    public Task<IReadOnlyList<System.Text.Json.JsonElement>> LogLinesAsync(string message) => Task.FromResult(LogLines(message));

    public async Task<IReadOnlyList<System.Text.Json.JsonElement>> SettledLogLinesAsync(string message)
    {
        // The captured log is in process; a line is there within milliseconds of being written.
        for (int attempt = 0; attempt < 250 && LogLines(message).Count == 0; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        IReadOnlyList<System.Text.Json.JsonElement> lines = LogLines(message);
        Assert.True(lines.Count > 0, $"no gateway line starts \"{message}\".");
        return lines;
    }

    /// <summary>
    /// Expiry is made at the provider, which issues the login's token already expired: the token
    /// library and the mock API read the system clock (evidence T-B10). Later tokens are valid.
    /// </summary>
    public async Task SignInWithExpiredAccessTokenAsync(Profile profile, HarnessApp app)
    {
        Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await profile.SignInAsync(app);
        Harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// The test provider approves every authorization at once, so a browser left alone would sign
    /// straight back in. The load balancer holds the next callback instead: the browser stops just
    /// after the provider's authorization endpoint, which is its login page.
    /// </summary>
    public async Task StopAtProviderLoginAsync(Profile profile, Func<Task> action)
    {
        LoadBalancer.Hold(url => url.AbsolutePath == "/signin-oidc");
        try
        {
            await action();
            await Assertions.Expect(profile.Page).ToHaveTitleAsync(TlsLoadBalancer.HeldTitle);
        }
        finally
        {
            LoadBalancer.Release();
        }
    }

    public Task<string> ReleaseOfAsync(string appId) => Task.FromResult(Harness.Options.Release);

    public Task PublishReleaseAsync(string appId, string release, IReadOnlyDictionary<string, byte[]> files) =>
        Harness.PublishReleaseAsync(appId, release, files, TestContext.Current.CancellationToken);

    /// <summary>A new registry revision, written and polled at once (Gateway.TestHarness), as <c>spactl release activate</c> writes one.</summary>
    public async Task SwitchReleaseAsync(HarnessApp app, string release)
    {
        Platform.Registry.RefreshOutcome outcome = await Harness.UpdateRegistryAsync(document =>
        {
            foreach (System.Text.Json.Nodes.JsonNode? entry in document["apps"]!.AsArray())
            {
                if (entry!["appId"]!.GetValue<string>() == app.AppId)
                {
                    entry["release"] = release;
                }
            }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Platform.Registry.RefreshOutcome.Swapped, outcome);
    }

    /// <summary>The harness polls as it writes, so the swap is done when <see cref="SwitchReleaseAsync"/> returns.</summary>
    public TimeSpan RegistryPropagation => TimeSpan.FromSeconds(5);

    /// <summary>A fresh browser profile: no cookies, no storage.</summary>
    public async Task<Profile> OpenAsync()
    {
        // The stand-in's certificate is this run's own, trusted by no store (TlsLoadBalancer).
        IBrowserContext context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        IPage page = await context.NewPageAsync();
        return new Profile(this, context, page);
    }

    /// <summary>
    /// A request no browser would send, through the same load balancer: the D tier's raw HTTP client
    /// (docs/plan/04-tests.md -> "How the suites run").
    /// </summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request) => LoadBalancer.SendRawAsync(request);

    /// <summary>The gateway's log lines, as JSON, whose <c>msg</c> starts with <paramref name="message"/>.</summary>
    public IReadOnlyList<System.Text.Json.JsonElement> LogLines(string message) =>
        [.. Harness.Log.Json.Where(line =>
            line.TryGetProperty("msg", out System.Text.Json.JsonElement msg)
            && msg.GetString()?.StartsWith(message, StringComparison.Ordinal) == true)];

    /// <summary>The request lines the gateway has written so far: one per request, <c>msg</c> of <c>request</c>.</summary>
    public IReadOnlyList<System.Text.Json.JsonElement> RequestLines =>
        [.. Harness.Log.Json.Where(line => line.TryGetProperty("msg", out System.Text.Json.JsonElement msg) && msg.GetString() == "request")];

    /// <summary>
    /// Waits for a request line after the first <paramref name="after"/> that carries
    /// <paramref name="rule"/>, and fails if none comes. The rule id is in the log line and nowhere a
    /// browser can see (contracts/request-gate.md -> "Rule ids"), and the line is written after the
    /// response, so the browser may be ahead of it (contracts/telemetry.md).
    /// </summary>
    public async Task AssertRuleLoggedAsync(int after, string rule, string? appId = null)
    {
        bool Logged() => RequestLines.Skip(after).Any(line =>
            line.TryGetProperty("rule", out System.Text.Json.JsonElement value) && value.GetString() == rule
            && (appId is null || (line.TryGetProperty("app_id", out System.Text.Json.JsonElement app) && app.GetString() == appId)));

        for (int attempt = 0; attempt < 250 && !Logged(); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(Logged(), $"no request line after the first {after} carries rule {rule}{(appId is null ? "" : " for " + appId)}.");
    }

    public async ValueTask DisposeAsync()
    {
        await browser.DisposeAsync();
        await LoadBalancer.DisposeAsync();
        await Harness.DisposeAsync();
    }
}
