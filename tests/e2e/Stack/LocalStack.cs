using Gateway.TestHarness;
using Microsoft.Playwright;
using Platform.Testing;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The stack a browser test runs against, locally: the hermetic cell (task 6.1) with a session
/// required, as the shipped gateway requires one, behind a <see cref="TlsLoadBalancer"/>, and a
/// Chromium whose resolver sends every app host to that listener, as the tunnel and the hosts file do
/// in the deployed tier (docs/plan/05-infrastructure-and-cost.md). One per test: each starts with an
/// empty provider log, an empty session store and a browser that has seen nothing.
/// </summary>
public sealed class LocalStack : IAsyncDisposable
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

    /// <summary>The parent domain every app host sits under, without the leading dot.</summary>
    public static string Domain => TestApps.HostSuffix.TrimStart('.');

    public static async Task<LocalStack> StartAsync(GatewayHarnessOptions? options = null)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GatewayHarness harness = await GatewayHarness.StartAsync((options ?? new GatewayHarnessOptions()) with { SessionRequired = true }, ct);
        try
        {
            TlsLoadBalancer balancer = await TlsLoadBalancer.StartAsync(harness.GatewayAddress, Domain, ct);
            // Every name under the app domain, whatever port its URL names, reaches the load balancer.
            // Nothing else is mapped, so the provider on loopback is reached directly, as it is on the
            // internet in the deployed tier.
            IBrowser browser = await Chromium.LaunchAsync($"--host-resolver-rules=MAP *.{Domain} {balancer.Endpoint}");
            return new LocalStack(harness, balancer, browser);
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public HarnessApp App(string appId) => Harness.App(appId);

    /// <summary>A fresh browser profile: no cookies, no storage.</summary>
    public async Task<Profile> OpenAsync()
    {
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

    public async ValueTask DisposeAsync()
    {
        await browser.DisposeAsync();
        await LoadBalancer.DisposeAsync();
        await Harness.DisposeAsync();
    }
}
