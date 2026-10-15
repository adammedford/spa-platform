using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Platform.Telemetry;

/// <summary>
/// Workstream 6's entry points: <see cref="AddPlatformTelemetry"/> first in the service table and
/// <see cref="UsePlatformTelemetry"/> at stage 2 of <c>contracts/pipeline.md</c>. Task 6.4.
/// </summary>
public static class PlatformTelemetryExtensions
{
    /// <summary>
    /// JSON log lines on stdout with the never-logged list held (contracts/telemetry.md), and
    /// OpenTelemetry traces and metrics. Phase 1 exports the log stream only: the OTLP exporter is
    /// switched on by <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, which Phase 1 leaves unset.
    /// </summary>
    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // stdout is the one log stream (contracts/telemetry.md -> What Phase 1 exports). The default
        // set also has the debug and EventSource providers, which would carry every line past the
        // formatter that holds the never-logged list.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = PlatformJsonFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<PlatformJsonFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

        foreach ((string category, LogLevel floor) in LogFloors.All)
        {
            builder.Logging.AddFilter(category, floor);
        }

        builder.Services.AddSingleton<RequestTelemetry>();

        string? otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        string? cell = builder.Configuration["Platform:Cell"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService("spa-gateway");
                if (cell is not null)
                {
                    resource.AddAttributes([new KeyValuePair<string, object>("cell", cell)]);
                }
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                        // The instrumentation already replaces query values with "Redacted"
                        // (https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNetCore/README.md);
                        // dropping the tag keeps that true if the switch that turns it off is set.
                        options.EnrichWithHttpRequest = (activity, _) => activity.SetTag("url.query", null))
                    .AddHttpClientInstrumentation(options =>
                        options.EnrichWithHttpRequestMessage = (activity, request) =>
                        {
                            if (request.RequestUri is { } uri)
                            {
                                activity.SetTag("url.full", uri.GetLeftPart(UriPartial.Path));
                            }
                        })
                    // contracts/telemetry.md -> The instruments: platform sources are named
                    // "Platform.<Project>"; YARP's own source names the forwarding span.
                    .AddSource("Platform.*")
                    .AddSource("Yarp.ReverseProxy");
                if (!string.IsNullOrWhiteSpace(otlp))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // contracts/telemetry.md -> The instruments: "Platform.<Project>" and nothing else.
                    .AddMeter("Platform.*");
                if (!string.IsNullOrWhiteSpace(otlp))
                {
                    metrics.AddOtlpExporter();
                }
            });

        return builder;
    }

    /// <summary>
    /// The request log line and the trace scope. It replaces the framework's default request log,
    /// which includes the query string: that log's category is held at Warning by
    /// <see cref="AddPlatformTelemetry"/>, and this stage writes the line in its place.
    /// </summary>
    public static IApplicationBuilder UsePlatformTelemetry(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RequestTelemetry telemetry = app.ApplicationServices.GetRequiredService<RequestTelemetry>();
        return app.Use(next => context => telemetry.InvokeAsync(context, next));
    }
}
