using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// The spans and metrics one harness's gateway produced, read in process.
/// <c>contracts/telemetry.md</c> -> What Phase 1 exports: "Tests read spans and metrics through the
/// in-memory exporter", and Phase 1 leaves <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> unset, so no collector
/// runs here and none has to.
///
/// <para>Wired through <see cref="GatewayHarnessOptions.ConfigureServices"/>, which is the harness's
/// own door for a test's service replacements, so the harness itself needs no change.</para>
///
/// <para><b>What the lists hold.</b> An OpenTelemetry provider subscribes to <see cref="ActivitySource"/>
/// and <see cref="Meter"/> by name, process-wide, so these lists also hold the spans and metrics of
/// every other harness alive in the same test process, and of the test browser's own
/// <c>HttpClient</c>. A test must therefore pick its request out by trace id, which it takes from the
/// request log line in that harness's own <see cref="GatewayHarness.Log"/>, and may not assert on how
/// many spans there are. Assertions about a name's shape are safe either way, which is what
/// <see cref="FixtureFiles.PlatformInstrumentNames"/> is for.</para>
///
/// <para>The exporters below are written here rather than taken from
/// <c>OpenTelemetry.Exporter.InMemory</c>: that package's exporter calls <c>ICollection.Add</c> with
/// no synchronisation
/// (https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry.Exporter.InMemory/InMemoryExporter.cs),
/// and a reader here enumerates while concurrent requests are still exporting. Task 6.4's own
/// <c>TelemetryHost</c> took the same way out for the same reason, in a project that does reference
/// that package. The reason is the synchronisation and nothing else: the package is pinned in
/// <c>Directory.Packages.props</c> already, and AGENTS.md -> Build and test lets a workstream add
/// <c>&lt;PackageReference Include="Name" /&gt;</c> with no version to its own project file, so
/// taking it would have needed no integrator edit either.</para>
/// </summary>
public sealed class CapturedTelemetry
{
    private readonly ConcurrentQueue<Activity> spans = new();
    private readonly ConcurrentQueue<Metric> metrics = new();

    /// <summary>Every span exported so far, as a snapshot.</summary>
    public IReadOnlyList<Activity> Spans => [.. spans];

    /// <summary>Every metric collected by the last <see cref="Flush"/>, as a snapshot.</summary>
    public IReadOnlyList<Metric> Metrics => [.. metrics];

    /// <summary>Harness options that capture the gateway's spans and metrics, and nothing else.</summary>
    public GatewayHarnessOptions Options(GatewayHarnessOptions? options = null)
    {
        CapturedTelemetry captured = this;
        options ??= new GatewayHarnessOptions();
        Action<IServiceCollection>? theirs = options.ConfigureServices;
        return options with
        {
            ConfigureServices = services =>
            {
                theirs?.Invoke(services);

                // Simple processing exports each span as it stops, without a batch timer. Tests
                // await their specific trace: the server span can stop after the response arrives.
                services.ConfigureOpenTelemetryTracerProvider(tracing =>
                    tracing.AddProcessor(new SimpleActivityExportProcessor(new SpanExporter(captured.spans))));
                services.ConfigureOpenTelemetryMeterProvider(meters =>
                    meters.AddReader(new BaseExportingMetricReader(new MetricExporter(captured.metrics))));
            },
        };
    }

    /// <summary>Collects the metric readers, so <see cref="Metrics"/> holds what the instruments hold.</summary>
    public void Flush(GatewayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        harness.Services.GetService<TracerProvider>()?.ForceFlush();
        harness.Services.GetService<MeterProvider>()?.ForceFlush();
    }

    /// <summary>The spans of one trace, which is how a test finds its own request's.</summary>
    public IEnumerable<Activity> Trace(string traceId) =>
        Spans.Where(span => span.TraceId.ToHexString() == traceId);

    /// <summary>
    /// Waits for <paramref name="ready"/>, which is how a test waits for the request log line and the
    /// spans of a request whose response it has already read: the telemetry stage writes the line after the rest of
    /// the pipeline has returned (contracts/telemetry.md), and the server span stops after that again.
    /// It fails rather than returning when the wait runs out, so a missing line is never a pass.
    /// </summary>
    public static async Task UntilAsync(Func<bool> ready, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ready);
        for (int attempt = 0; attempt < 250 && !ready(); attempt++)
        {
            await Task.Delay(20, ct);
        }

        Assert.True(ready(), $"waited five seconds for {what}, which never arrived.");
    }

    /// <summary>The request log lines the telemetry stage wrote, parsed (<c>msg</c> is <c>request</c>).</summary>
    public static IReadOnlyList<JsonElement> RequestLines(GatewayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return [.. harness.Log.Json.Where(line =>
            line.TryGetProperty("msg", out JsonElement message) && message.GetString() == "request")];
    }

    private sealed class SpanExporter(ConcurrentQueue<Activity> destination) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity span in batch)
            {
                destination.Enqueue(span);
            }

            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter(ConcurrentQueue<Metric> destination) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                destination.Enqueue(metric);
            }

            return ExportResult.Success;
        }
    }
}
