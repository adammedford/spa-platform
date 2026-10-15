using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Workstream 4's own gateway, cut down to the two entry points the pipeline contract gives task
/// 4.1: <c>AddPlatformProxy</c> and <c>MapPlatformProxy</c>. No other stage is here, because no
/// other stage is this task's, and the composed suites in workstream 6 run the whole pipeline.
///
/// It listens on loopback and the test sets the <c>Host</c> header itself. Everything here routes by
/// host, so a host a client cannot choose would prove nothing.
///
/// The composed gateway cannot show any of this yet: the host-resolution stub at stage 4 answers 404
/// to every host until task 2.3, so nothing after it runs. Workstream 6 proves these behaviours again
/// in the composed suites once it does.
/// </summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<SettableRequestTelemetryFeature> telemetryFeatures;

    private ProxyHost(
        WebApplication app, Uri baseAddress, InMemoryRegistry registry,
        List<SettableRequestTelemetryFeature> telemetryFeatures)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Registry = registry;
        this.telemetryFeatures = telemetryFeatures;
    }

    /// <summary>Where the gateway is listening. The <c>Host</c> header is the test's to set.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The registry the routes come from. Swap it to move the routes on.</summary>
    public InMemoryRegistry Registry { get; }

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered the telemetry middleware, with null for a request that left none. Empty unless
    /// the host was started with <c>telemetry: true</c>, because stage 2 is the only thing that creates
    /// that feature and this host is cut down to one stage.
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

    public static Task<ProxyHost> StartAsync(CancellationToken ct, params AppDefinition[] apps) =>
        StartAsync(ct, telemetry: false, apps);

    /// <param name="telemetry">
    /// true installs stage 2's stand-in, a <see cref="SettableRequestTelemetryFeature"/> per request,
    /// and records what each request left on it in <see cref="TelemetryRules"/>. Off in the overload
    /// above, so every other test here runs stage 10 with no feature installed, which is what holds
    /// the stage's write to being null-conditional.
    /// </param>
    public static async Task<ProxyHost> StartAsync(CancellationToken ct, bool telemetry, params AppDefinition[] apps)
    {
        InMemoryRegistry registry = new(InMemoryRegistry.SnapshotOf(1, "\"etag\"", apps));

        WebApplicationBuilder builder = Loopback.Builder();
        builder.Services.AddSingleton<IRegistry>(registry);
        builder.AddPlatformProxy();

        WebApplication web = builder.Build();
        List<SettableRequestTelemetryFeature> telemetryFeatures = [];

        // Stage 2's stand-in, when a test asks for it: the feature stage 2 sets on the request before
        // calling the rest of the pipeline and reads back once it returns
        // (contracts/telemetry.md -> "How `rule` and `session_ref` reach the request line"). It is off
        // by default, so every other test here runs the stage with no feature installed, which is what
        // holds the stage's write to being null-conditional.
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

        // As Program.cs does it: stage 7 is the framework's own UseRouting, and the Map entry points
        // of stage 10 come after it.
        web.UseRouting();
        web.MapPlatformProxy();

        await web.StartAsync(ct);

        return new ProxyHost(web, Loopback.AddressOf(web, "gateway"), registry, telemetryFeatures);
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
