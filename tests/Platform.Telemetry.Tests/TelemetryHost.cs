using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Telemetry.Tests;

/// <summary>
/// Workstream 6's own gateway, cut down to <c>UsePlatformTelemetry</c> and what stands either side of
/// it, built on <see cref="StageHost"/> (#140, #152): the telemetry entry points, a stand-in for host
/// resolution that pins <c>app1</c> as <see cref="IAppFeature"/>, a stand-in for the later stages
/// that write <see cref="IRequestTelemetryFeature"/>, the framework's authentication with one real
/// OpenID Connect scheme, and a terminal that calls an upstream over a real socket with the
/// request's own query string.
///
/// The stage host's telemetry stand-in stays off: the real <c>UsePlatformTelemetry</c> is the first
/// thing in the pipeline delegate. <c>AddPlatformTelemetry</c> clears the providers and adds the
/// console logger back inside the services delegate, and the stage host registers its capture after
/// that, so <see cref="CapturedLog.Rendered"/> and <see cref="CapturedLog.Json"/> carry what stdout
/// would, rendered by the platform's JSON formatter found by its configured name.
///
/// <paramref name="telemetry"/> false is the control: the same host and the same traffic with no
/// telemetry entry points and no console logger, so the capture holds only the plain records
/// (<see cref="CapturedLog.Lines"/>). It is what shows the canaries do reach a log with no
/// telemetry, so N-16's "none appear" can fail.
/// </summary>
public sealed class TelemetryHost : IAsyncDisposable
{
    public const string AppId = "app1";

    /// <summary>The session cookie the later-stage stand-in reads its session key from.</summary>
    public const string SessionCookie = "__Host-spa-session";

    /// <summary>A header naming the rule id the later-stage stand-in rejects the request under.</summary>
    public const string RejectHeader = "X-Test-Reject";

    /// <summary>
    /// A header naming the class of key the later-stage stand-in reports a refused object read for,
    /// <c>asset</c> or <c>shell</c>, as the asset route writes it (change request
    /// contracts/changes/issue-381-object-read-refusal.md, item 5).
    /// </summary>
    public const string ReadRefusedHeader = "X-Test-Read-Refused";

    private readonly StageHost host;
    private readonly EchoUpstreamListener upstream;
    private readonly ConcurrentQueue<Activity> spans;

    private TelemetryHost(StageHost host, EchoUpstream echo, EchoUpstreamListener upstream,
        ConcurrentQueue<Activity> spans, List<Metric> metrics)
    {
        this.host = host;
        this.upstream = upstream;
        this.spans = spans;
        Upstream = echo;
        Metrics = metrics;
    }

    // The pinned in-memory exporter calls ICollection.Add without synchronizing readers:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry.Exporter.InMemory/InMemoryExporter.cs
    private sealed class CapturedActivityExporter(ConcurrentQueue<Activity> destination) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch)
            {
                destination.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }

    public Uri BaseAddress => host.BaseAddress;

    /// <summary>The stage host's capture: plain records always, and the rendered JSON lines when telemetry is on.</summary>
    public CapturedLog Log => host.Log;

    // Export callbacks can append after a response reaches the client; readers need a stable
    // snapshot for N-16 and F-11 instead of enumerating a list while another host exports.
    public IReadOnlyList<Activity> Spans => spans.ToArray();

    public List<Metric> Metrics { get; }

    /// <summary>The upstream's recorder, which saw what the terminal sent.</summary>
    public EchoUpstream Upstream { get; }

    public IServiceProvider Services => host.Services;

    public static async Task<TelemetryHost> StartAsync(
        CancellationToken ct,
        bool telemetry = true,
        IDictionary<string, string?>? configuration = null,
        string unknownHostRule = "host.unknown",
        string appId = AppId)
    {
        EchoUpstream echo = new();
        EchoUpstreamListener listener = await echo.ListenAsync(ct);

        ConcurrentQueue<Activity> spans = new();
        List<Metric> metrics = [];
        // The app host resolution pins. Overridable so a metric test can pick out its own points by
        // app_id among the instruments every host in the test process reports to.
        AppDefinition entry = TestApps.Entry(appId);

        StageHost host;
        try
        {
            host = await StageHost.StartAsync(new StageHostOptions
            {
                Services = builder => ConfigureServices(builder, telemetry, configuration, listener.BaseAddress, spans, metrics),
                Pipeline = web => ComposePipeline(web, telemetry, entry, unknownHostRule),
            }, ct);
        }
        catch
        {
            await listener.DisposeAsync();
            throw;
        }

        return new TelemetryHost(host, echo, listener, spans, metrics);
    }

    private static void ConfigureServices(
        WebApplicationBuilder builder,
        bool telemetry,
        IDictionary<string, string?>? configuration,
        Uri upstreamAddress,
        ConcurrentQueue<Activity> spans,
        List<Metric> metrics)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Cell"] = "test-cell",
        });
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        if (telemetry)
        {
            // Clears the providers and adds the console logger with the platform's JSON formatter;
            // the stage host registers its capture after this delegate, so the capture survives.
            builder.AddPlatformTelemetry();
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(new CapturedActivityExporter(spans))));
            builder.Services.ConfigureOpenTelemetryMeterProvider(meters => meters.AddInMemoryExporter(metrics));
        }

        builder.Services.AddHttpClient("upstream", client => client.BaseAddress = upstreamAddress);

        // One real OpenID Connect scheme, so the callback below reaches the shipped handler, which
        // logs the full callback URL at Trace (ADR-0007 -> Logging rules this forces). The
        // configuration is static, so nothing is fetched.
        builder.Services.AddAuthentication()
            .AddCookie("cookie:" + AppId)
            .AddOpenIdConnect("oidc:" + AppId, options =>
            {
                options.SignInScheme = "cookie:" + AppId;
                options.ClientId = "client-" + AppId;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.invalid",
                    AuthorizationEndpoint = "https://idp.invalid/authorize",
                    TokenEndpoint = "https://idp.invalid/token",
                };
            });
    }

    private static void ComposePipeline(WebApplication web, bool telemetry, AppDefinition entry, string unknownHostRule)
    {
        if (telemetry)
        {
            web.UsePlatformTelemetry();
        }

        // Stand-in for host resolution: app1 on every host except unknown.invalid, which gets the 404 and
        // the host.unknown rule id (overridable to isolate metric tests) the real stage gives an
        // unknown host (change request 6.4 ->
        // Decision). Written null-conditionally, as every real stage writes it.
        web.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Unknown-Host"].Count > 0)
            {
                context.Features.Get<IRequestTelemetryFeature>()?.Rule = unknownHostRule;
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Features.Set<IAppFeature>(SettableAppFeature.For(entry));
            await next(context);
        });

        // Stand-in for the later stages. Workstream 3 turns the session cookie's key into its
        // reference, and the key goes no further; a request gate rejects under the rule id the test
        // names, with the 403 the gate answers.
        web.Use(async (context, next) =>
        {
            IRequestTelemetryFeature? fields = context.Features.Get<IRequestTelemetryFeature>();
            if (context.Request.Cookies.TryGetValue(SessionCookie, out string? key) && !string.IsNullOrEmpty(key))
            {
                fields?.SessionRef = SessionRef.Of(key);
            }

            if (context.Request.Headers[RejectHeader] is [{ } rule, ..])
            {
                fields?.Rule = rule;
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // The asset route's refused read: the class of key, never the key, and the 404 a refused
            // asset answers. The telemetry stage reads the class and not the status.
            if (context.Request.Headers[ReadRefusedHeader] is [{ } refused, ..])
            {
                fields?.ReadRefused = refused;
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        web.UseAuthentication();

        web.Run(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                HttpClient client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("upstream");
                using HttpResponseMessage response = await client.GetAsync(
                    "/items" + context.Request.QueryString.Value, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                return;
            }

            // /status/{code} answers with that code, so a test can pick out its own requests among
            // instruments that every host in the test process reports to.
            context.Response.StatusCode = context.Request.Path.StartsWithSegments("/status", out PathString code)
                ? int.Parse(code.Value!.TrimStart('/'), System.Globalization.CultureInfo.InvariantCulture)
                : StatusCodes.Status200OK;
        });
    }

    /// <summary>Exports what the providers hold, so spans and metrics are in the lists.</summary>
    public void Flush()
    {
        host.Services.GetService<TracerProvider>()?.ForceFlush();
        host.Services.GetService<MeterProvider>()?.ForceFlush();
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await upstream.DisposeAsync();
    }
}
