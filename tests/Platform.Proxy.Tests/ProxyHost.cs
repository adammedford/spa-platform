using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
/// It is built on <see cref="StageHost"/> (#140), which owns the listener, the telemetry stand-in, the
/// captured log and the client; this host is its services delegate, its pipeline delegate and its
/// requests. It listens on loopback and the test sets the <c>Host</c> header itself. Everything here
/// routes by host, so a host a client cannot choose would prove nothing.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly StageHost host;
    private readonly RecordingLoggerProvider recorded;

    private ProxyHost(StageHost host, InMemoryRegistry registry, FakeAccessTokenSource tokens, RecordingLoggerProvider recorded)
    {
        this.host = host;
        Registry = registry;
        Tokens = tokens;
        this.recorded = recorded;
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
    public Uri BaseAddress => host.BaseAddress;

    /// <summary>The registry the routes come from. Swap it to move the routes on.</summary>
    public InMemoryRegistry Registry { get; }

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered, with null for a request that left none. Empty unless the host was started
    /// with <c>telemetry: true</c>, because <c>UsePlatformTelemetry</c> is the only thing that creates
    /// that feature and this host does not run it.
    /// </summary>
    public IReadOnlyList<string?> TelemetryRules => host.TelemetryRules;

    /// <summary>Every line the gateway logged, captured at Trace.</summary>
    public CapturedLog Log => host.Log;

    /// <summary>
    /// Every line the gateway logged with its event id, which <see cref="CapturedLine"/> does not carry.
    /// Read by the one test that holds the unknown-api line to its <c>LoggerMessage</c> template (#162),
    /// and goes when <see cref="CapturedLine"/> carries one (change request issue-148-captured-line-event-id).
    /// </summary>
    internal IReadOnlyList<LogLine> EventLines => recorded.Lines;

    public static Task<ProxyHost> StartAsync(CancellationToken ct, params AppDefinition[] apps) =>
        StartAsync(ct, telemetry: false, apps);

    public static Task<ProxyHost> StartAsync(CancellationToken ct, bool telemetry, params AppDefinition[] apps) =>
        StartAsync(ct, new ProxyHostOptions { Telemetry = telemetry }, apps);

    /// <param name="options">
    /// <see cref="ProxyHostOptions.Telemetry"/> true asks the stage host for <c>UsePlatformTelemetry</c>'s
    /// stand-in, and what each request left on it is in <see cref="TelemetryRules"/>. Off by default, so
    /// every other test here runs <c>MapPlatformProxy</c> with no feature installed, as a stage's own test
    /// project does. That the write is null-conditional is <c>Rejection</c>'s, proven once in
    /// tests/Platform.Abstractions.Tests (#142, Q10).
    /// </param>
    public static async Task<ProxyHost> StartAsync(CancellationToken ct, ProxyHostOptions options, params AppDefinition[] apps)
    {
        ArgumentNullException.ThrowIfNull(options);
        InMemoryRegistry registry = new(InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps));
        FakeAccessTokenSource tokens = new() { Result = new AccessTokenResult.Token(DefaultToken) };
        RecordingLoggerProvider recorded = new();

        StageHost host = await StageHost.StartAsync(
            new StageHostOptions
            {
                TelemetryStandIn = options.Telemetry,
                Services = builder =>
                {
                    builder.Logging.AddProvider(recorded);
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
                },
                Pipeline = web =>
                {
                    // Host resolution's stand-in: the app whose host the request names, pinned as
                    // IAppFeature, which is what the token source reads to know whose session it is. An
                    // unknown host is left without one, as the real stage would never let it this far.
                    // The pin matches the request's host without its port, while the real stage compares
                    // the full authority, port included (#148); every request here names a host with no
                    // port, so the two agree on what these tests send.
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
                },
            },
            ct);

        return new ProxyHost(host, registry, tokens, recorded);
    }

    /// <summary>A request to this gateway, addressed to one app's host.</summary>
    public HttpRequestMessage Request(HttpMethod method, string host, string pathAndQuery)
    {
        HttpRequestMessage request = new(method, new Uri(BaseAddress, pathAndQuery));
        request.Headers.Host = host;
        return request;
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
