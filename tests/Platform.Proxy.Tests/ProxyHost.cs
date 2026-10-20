using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Security;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Workstream 4's own gateway, cut down to the proxy's two entry points: <c>AddPlatformProxy</c> and
/// <c>MapPlatformProxy</c>. Beside them are stand-ins for host resolution (the app feature) and for
/// workstream 3's token source, and <c>UsePlatformSecurityHeaders</c> when a test asks for it
/// (<see cref="ProxyHostOptions"/>). The composed suites in workstream 6 run the whole pipeline.
///
/// It listens on loopback and the test sets the <c>Host</c> header itself. Everything here routes by
/// host, so a host a client cannot choose would prove nothing.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<SettableRequestTelemetryFeature> telemetryFeatures;
    private readonly RecordingLoggerProvider log;

    private ProxyHost(
        WebApplication app, Uri baseAddress, InMemoryRegistry registry,
        List<SettableRequestTelemetryFeature> telemetryFeatures, FakeAccessTokenSource tokens,
        RecordingLoggerProvider log)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Registry = registry;
        this.telemetryFeatures = telemetryFeatures;
        Tokens = tokens;
        this.log = log;
    }

    /// <summary>The token placed on a forwarded call when a test arranges nothing.</summary>
    public const string DefaultToken = "token-for-the-session";

    /// <summary>
    /// The token source the proxy asks, standing in for workstream 3's. Unlike the double's
    /// own default, this host starts it on <see cref="DefaultToken"/>, so a test about routing gets a
    /// forward rather than the 401; a test about the 401 arranges it.
    /// </summary>
    public FakeAccessTokenSource Tokens { get; }

    /// <summary>Where the gateway is listening. The <c>Host</c> header is the test's to set.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The registry the routes come from. Swap it to move the routes on.</summary>
    public InMemoryRegistry Registry { get; }

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered the telemetry middleware, with null for a request that left none. Empty unless
    /// the host was started with <c>telemetry: true</c>, because <c>UsePlatformTelemetry</c> is the only thing that
    /// creates that feature and this host does not run it.
    /// </summary>
    public IReadOnlyList<string?> TelemetryRules
    {
        get
        {
            lock (telemetryFeatures)
            {
                return [.. telemetryFeatures.Select(feature => feature.Rule)];
            }
        }
    }

    /// <summary>Every line the gateway logged, in the order written.</summary>
    internal IReadOnlyList<LogLine> Log => log.Lines;

    public static Task<ProxyHost> StartAsync(CancellationToken ct, params AppDefinition[] apps) =>
        StartAsync(ct, telemetry: false, apps);

    public static Task<ProxyHost> StartAsync(CancellationToken ct, bool telemetry, params AppDefinition[] apps) =>
        StartAsync(ct, new ProxyHostOptions { Telemetry = telemetry }, apps);

    /// <param name="telemetry">
    /// true installs <c>UsePlatformTelemetry</c>'s stand-in, a <see cref="SettableRequestTelemetryFeature"/> per request,
    /// and records what each request left on it in <see cref="TelemetryRules"/>. Off in the overload
    /// above, so every other test here runs <c>MapPlatformProxy</c> with no feature installed, as a stage's own
    /// test project does. That the write is null-conditional is <c>Rejection</c>'s, proven once in
    /// tests/Platform.Abstractions.Tests (#142, Q10).
    /// </param>
    public static async Task<ProxyHost> StartAsync(CancellationToken ct, ProxyHostOptions options, params AppDefinition[] apps)
    {
        bool telemetry = options.Telemetry;
        InMemoryRegistry registry = new(InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps));
        FakeAccessTokenSource tokens = new() { Result = new AccessTokenResult.Token(DefaultToken) };

        WebApplicationBuilder builder = Loopback.Builder();
        RecordingLoggerProvider log = new();
        builder.Logging.AddProvider(log);
        builder.Services.AddSingleton<IRegistry>(registry);
        if (options.TokenSource)
        {
            builder.Services.AddSingleton<IAccessTokenSource>(tokens);
        }

        if (options.SecurityHeaders)
        {
            builder.Configuration["Issuers:primary:BrowserOrigin"] = ProxyHostOptions.BrowserOrigin;
        }

        builder.AddPlatformProxy();

        WebApplication web = builder.Build();
        List<SettableRequestTelemetryFeature> telemetryFeatures = [];

        // UsePlatformTelemetry's stand-in, when a test asks for it: the feature it sets on the request before
        // calling the rest of the pipeline and reads back once it returns
        // (contracts/telemetry.md -> "How `rule` and `session_ref` reach the request line"). It is off
        // by default, so every other test here runs the stage with no feature installed; the write is
        // Rejection's and null-conditional, proven in tests/Platform.Abstractions.Tests (#142, Q10).
        //
        // The feature is recorded before the request runs rather than after: each stage sets the rule
        // id before it writes the response, so a test reading the recorded feature cannot race the
        // response body back to the client.
        if (telemetry)
        {
            web.Use((context, next) =>
            {
                SettableRequestTelemetryFeature feature = new();
                lock (telemetryFeatures)
                {
                    telemetryFeatures.Add(feature);
                }

                context.Features.Set<IRequestTelemetryFeature>(feature);
                return next(context);
            });
        }

        // Host resolution's stand-in: the app whose host the request names, pinned as IAppFeature, which
        // is what the token source reads to know whose session it is. An unknown host is left without
        // one, as the real stage would never let it this far.
        web.Use((context, next) =>
        {
            if (registry.Current.ByHost.TryGetValue(context.Request.Host.Host, out AppDefinition? app))
            {
                context.Features.Set<IAppFeature>(SettableAppFeature.For(app));
            }

            return next(context);
        });

        if (options.SecurityHeaders)
        {
            web.UsePlatformSecurityHeaders();
        }

        // As Program.cs does it: the framework's own UseRouting, and the Map entry points after it.
        web.UseRouting();
        web.MapPlatformProxy();

        await web.StartAsync(ct);

        return new ProxyHost(web, Loopback.AddressOf(web, "gateway"), registry, telemetryFeatures, tokens, log);
    }

    /// <summary>A request to this gateway, addressed to one app's host.</summary>
    public HttpRequestMessage Request(HttpMethod method, string host, string pathAndQuery)
    {
        HttpRequestMessage request = new(method, new Uri(BaseAddress, pathAndQuery));
        request.Headers.Host = host;
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
