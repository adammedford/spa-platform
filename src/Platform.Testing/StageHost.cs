using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing.Internal;

namespace Platform.Testing;

/// <summary>
/// What a test host hands <see cref="StageHost.StartAsync"/>: its services, its pipeline, and whether
/// it wants the telemetry stand-in. Nothing stage-specific crosses this seam; trusted networks,
/// issuers and the like are the test host's, set in <see cref="Services"/>.
/// </summary>
public sealed class StageHostOptions
{
    /// <summary>
    /// The test host's own <c>Add*</c> entry points and configuration. Runs after the providers are
    /// cleared and the floor set to Trace, and before the capture is registered, so it may clear
    /// providers and add its own, as <c>AddPlatformTelemetry</c> does, and the capture survives it.
    /// </summary>
    public Action<WebApplicationBuilder>? Services { get; init; }

    /// <summary>
    /// The test host's own <c>Use*</c> and <c>Map*</c> entry points in the contract's order, its pin
    /// and its terminal. Runs on the built application, after the telemetry stand-in when one is asked.
    /// </summary>
    public required Action<WebApplication> Pipeline { get; init; }

    /// <summary>
    /// true installs the telemetry stand-in, outermost as <c>UsePlatformTelemetry</c> is. False by
    /// default, so a stage's null-conditional write of the feature stays proven (change request 6.4,
    /// decision 1).
    /// </summary>
    public bool TelemetryStandIn { get; init; }
}

/// <summary>
/// The Kestrel host a test host is built on (#140; change request issue-145): a loopback listener, the
/// telemetry stand-in when asked, the captured log, the client and the raw sender, composing the
/// pipeline the test host hands it. The module calls no <c>Add*</c>, <c>Use*</c> or <c>Map*</c> entry
/// point: those are frozen in <c>contracts/pipeline.md</c>, and composing them is the test host's job.
///
/// <para>The construction order is part of the interface, and callers rely on it:</para>
/// <list type="number">
/// <item>the slim builder, in the <c>Production</c> environment;</item>
/// <item>a loopback URL on port zero;</item>
/// <item>every logger provider cleared;</item>
/// <item>the minimum level set to Trace, configuration rules left in force;</item>
/// <item>the <see cref="StageHostOptions.Services"/> delegate;</item>
/// <item>the capture provider registered (<see cref="CapturedLog.ProviderFor"/>);</item>
/// <item><c>Build()</c>;</item>
/// <item>the telemetry stand-in, if asked;</item>
/// <item>the <see cref="StageHostOptions.Pipeline"/> delegate;</item>
/// <item>start, and the address read.</item>
/// </list>
/// </summary>
public sealed class StageHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<SettableRequestTelemetryFeature> telemetry;

    private StageHost(WebApplication app, Uri baseAddress, CapturedLog log, List<SettableRequestTelemetryFeature> telemetry)
    {
        this.app = app;
        this.telemetry = telemetry;
        BaseAddress = baseAddress;
        Log = log;

        // No cookie container: a test writes the Cookie header itself, case variants and all. No
        // redirects: a challenge is an answer the test reads (change request issue-145, section 6).
        Client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress,
        };
    }

    /// <summary>The loopback address Kestrel bound, with its port, e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The application's services.</summary>
    public IServiceProvider Services => app.Services;

    /// <summary>Every line the host logged, captured at Trace.</summary>
    public CapturedLog Log { get; }

    /// <summary>One client on <see cref="BaseAddress"/>, with no cookie container and no automatic redirect.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// The rule id each request left on its <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered, null where none was left. Empty unless the host was started with
    /// <see cref="StageHostOptions.TelemetryStandIn"/>.
    /// </summary>
    public IReadOnlyList<string?> TelemetryRules
    {
        get
        {
            lock (telemetry)
            {
                return [.. telemetry.Select(feature => feature.Rule)];
            }
        }
    }

    /// <summary>The last request's feature, or null when none entered or the stand-in is off.</summary>
    public SettableRequestTelemetryFeature? LastTelemetry
    {
        get
        {
            lock (telemetry)
            {
                return telemetry.Count == 0 ? null : telemetry[^1];
            }
        }
    }

    /// <summary>Starts a host in the construction order above.</summary>
    /// <exception cref="InvalidOperationException">The start yielded no address.</exception>
    public static async Task<StageHost> StartAsync(StageHostOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pipeline);

        WebApplicationBuilder builder = LoopbackHost.CreateBuilder();

        // The framework's default floor is Information, so a never-logged search could not see the
        // Debug and Trace lines the OpenID Connect handler writes its callback URL in (#140).
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        options.Services?.Invoke(builder);

        // After the Services delegate, so a delegate that clears the providers cannot remove it.
        CapturedLog log = new();
        builder.Services.AddSingleton<ILoggerProvider>(services => log.ProviderFor(services));

        WebApplication app = builder.Build();

        List<SettableRequestTelemetryFeature> telemetry = [];
        if (options.TelemetryStandIn)
        {
            // Before the Pipeline delegate, outermost as UsePlatformTelemetry is (contracts/pipeline.md,
            // ADR-0018 decision 1). The feature is recorded before the request runs: each stage sets its
            // rule id before it writes the response, so a test reading it cannot race the body back.
            app.Use((context, next) =>
            {
                SettableRequestTelemetryFeature feature = new();
                lock (telemetry)
                {
                    telemetry.Add(feature);
                }

                context.Features.Set<IRequestTelemetryFeature>(feature);
                return next(context);
            });
        }

        options.Pipeline(app);

        Uri address;
        try
        {
            address = await LoopbackHost.StartAsync(app, "the stage host", ct);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new StageHost(app, address, log, telemetry);
    }

    /// <summary>Sends <paramref name="request"/> to <see cref="BaseAddress"/> as written, through <see cref="RawHttp"/>.</summary>
    public Task<RawResponse> RawAsync(string request, CancellationToken ct = default) =>
        RawHttp.SendAsync(BaseAddress, request, ct);

    /// <summary>
    /// Sends <paramref name="method"/> on <paramref name="target"/> (exactly as written) to
    /// <see cref="BaseAddress"/>, with the given <c>Host</c>, any further headers in order, and
    /// <c>Connection: close</c>.
    /// </summary>
    public Task<RawResponse> RawAsync(
        string method,
        string target,
        string host,
        IEnumerable<(string Name, string Value)>? headers = null,
        CancellationToken ct = default) =>
        RawHttp.SendAsync(BaseAddress, method, target, host, headers, ct);

    /// <summary>Stops the listener, so a connect after it fails.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
