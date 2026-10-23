using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// Workstream 2's gateway cut down to its two middleware entry points, <c>UsePlatformHealth</c>
/// then <c>UsePlatformHostResolution</c>, over an <see cref="InMemoryRegistry"/> the test swaps by
/// hand. The in-memory registry isolates these stages from object-store configuration; workstream 6
/// proves them again in the composed gateway.
///
/// The listener, the log capture, the telemetry stand-in and the raw sender are
/// <see cref="StageHost"/>'s (#140, ticket #149); this class holds only what is hosting's own: the
/// stages it proves, in the contract's order, and the terminal behind them.
///
/// The terminal stands for everything after host resolution, <c>UsePlatformSecurityHeaders</c> to
/// the endpoints. It records the <see cref="IAppFeature"/> it was handed and answers 200 with
/// <see cref="Passed"/>, so a test that asserts a refusal can fail if
/// <c>UsePlatformHostResolution</c> let the request through. A test can give it work to do
/// mid-request through <see cref="DuringRequest"/>.
///
/// Given an <see cref="IObjectStore"/>, the terminal is the asset endpoint instead:
/// <c>UseRouting</c> then <c>MapPlatformAssets</c> over that store (task 2.4).
/// <c>UsePlatformSecurityHeaders</c> to <c>UsePlatformAntiforgery</c> are other workstreams' and
/// are left out, so what the endpoint refuses it refuses on its own.
/// </summary>
public sealed class HostingHost : IAsyncDisposable
{
    public const string Passed = "passed";

    private readonly StageHost stage;
    private readonly Terminal terminal;

    private HostingHost(StageHost stage, InMemoryRegistry registry, Terminal terminal)
    {
        this.stage = stage;
        this.terminal = terminal;
        Registry = registry;
    }

    public Uri BaseAddress => stage.BaseAddress;

    public InMemoryRegistry Registry { get; }

    /// <summary>Every line the host logged, captured at Trace.</summary>
    public CapturedLog Log => stage.Log;

    /// <summary>
    /// How many requests reached the terminal, which stands for every stage after host resolution.
    /// </summary>
    public int Reached => Volatile.Read(ref terminal.Reached);

    /// <summary>The feature the terminal last saw, read when it saw it.</summary>
    public (string AppId, string AppHost, long Revision)? LastSeen => terminal.LastSeen;

    /// <summary>Runs in the terminal before it reads the feature: a swap mid-request goes here.</summary>
    public Action? DuringRequest
    {
        get => terminal.DuringRequest;
        set => terminal.DuringRequest = value;
    }

    /// <summary>
    /// The <see cref="IRequestTelemetryFeature"/> the last request carried, for a host started with
    /// <c>telemetry: true</c>, and null for one started without. Reading it after the response is
    /// what <c>UsePlatformTelemetry</c> does with the real feature: it sets one on the request,
    /// calls the rest of the pipeline, then reads back the rule id whichever stage rejected left on
    /// it (contracts/telemetry.md -> "How <c>rule</c> and <c>session_ref</c> reach the request
    /// line").
    /// </summary>
    public SettableRequestTelemetryFeature? LastTelemetry => stage.LastTelemetry;

    /// <param name="telemetry">
    /// <c>true</c> asks <see cref="StageHost"/> for its telemetry stand-in, outermost as
    /// <c>UsePlatformTelemetry</c> is, which this project's pipeline does not have. Left
    /// <c>false</c> by default to exercise component pipelines without the feature, as required by
    /// contracts/changes/6.4-request-telemetry-feature.md.
    /// </param>
    /// <param name="instance">
    /// The instance <c>/readyz</c> names, registered before <c>AddPlatformHosting</c>; null leaves the
    /// entry point's own, which off ECS is an id generated for this host.
    /// </param>
    public static async Task<HostingHost> StartAsync(
        RegistrySnapshot initial,
        CancellationToken ct,
        IObjectStore? store = null,
        bool telemetry = false,
        GatewayInstance? instance = null)
    {
        InMemoryRegistry registry = new(initial);
        Terminal terminal = new();

        StageHost stage = await StageHost.StartAsync(
            new StageHostOptions
            {
                TelemetryStandIn = telemetry,
                Services = builder =>
                {
                    builder.Services.AddSingleton<IRegistry>(registry);
                    if (store is not null)
                    {
                        builder.Services.AddSingleton(store);
                    }

                    // Before AddPlatformHosting, as the harness gives one (change request P2-5, decision
                    // 2): the entry point registers its own only when none is there.
                    if (instance is not null)
                    {
                        builder.Services.AddSingleton(instance);
                    }

                    builder.AddPlatformHosting();
                },
                Pipeline = web =>
                {
                    web.UsePlatformHealth();
                    web.UsePlatformHostResolution();

                    if (store is not null)
                    {
                        // Explicit, and after UsePlatformHostResolution as in src/Gateway/Program.cs:
                        // a WebApplication with endpoints and no UseRouting call puts routing at the
                        // front of the pipeline, before health and host resolution.
                        web.UseRouting();
                        web.MapPlatformAssets();
                        return;
                    }

                    web.Run(context => terminal.RunAsync(context, ct));
                },
            },
            ct);

        return new HostingHost(stage, registry, terminal);
    }

    /// <summary>
    /// Sends <c>GET path</c> with the <c>Host</c> header given and any extra header lines, over a raw
    /// socket. Raw because the <c>Host</c> header is the subject (AGENTS.md: reach the gateway over a
    /// raw socket when the Host header matters), and <see cref="HttpClient"/> writes it from the URI.
    /// </summary>
    public Task<RawResponse> GetAsync(string host, string path, CancellationToken ct, string extraHeaders = "") =>
        SendAsync("GET", host, path, ct, extraHeaders);

    /// <summary>
    /// <see cref="GetAsync"/> with any method. The request line is sent byte for byte as given, so a
    /// test can put a dot segment, a backslash or a NUL in it that no HTTP client would send.
    /// <paramref name="extraHeaders"/> is whole header lines, each ending in CRLF.
    /// </summary>
    public Task<RawResponse> SendAsync(string method, string host, string path, CancellationToken ct, string extraHeaders = "") =>
        stage.RawAsync($"{method} {path} HTTP/1.1\r\nHost: {host}\r\n{extraHeaders}Connection: close\r\n\r\n", ct);

    public ValueTask DisposeAsync() => stage.DisposeAsync();

    /// <summary>
    /// The terminal's state. Built before the host, because the <see cref="StageHostOptions.Pipeline"/>
    /// delegate that installs the terminal runs inside <see cref="StageHost.StartAsync"/>.
    /// </summary>
    private sealed class Terminal
    {
        public int Reached;

        public (string AppId, string AppHost, long Revision)? LastSeen { get; private set; }

        public Action? DuringRequest { get; set; }

        public Task RunAsync(HttpContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Reached);
            DuringRequest?.Invoke();
            IAppFeature? feature = context.Features.Get<IAppFeature>();
            LastSeen = feature is null ? null : (feature.App.AppId, feature.App.Host, feature.Snapshot.Revision);
            return context.Response.WriteAsync(Passed, ct);
        }
    }
}
