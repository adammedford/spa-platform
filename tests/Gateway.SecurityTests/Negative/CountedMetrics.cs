using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The gateway's counters, read through the same in-memory export the functional suite reads them
/// by (<c>tests/Gateway.IntegrationTests/Functional/CapturedTelemetry.cs</c>):
/// <c>contracts/telemetry.md</c> -> What Phase 1 exports, "Tests read spans and metrics through the
/// in-memory exporter". It answers one question, "how much has this counter counted under these
/// tags", and a test asks it before and after its request and reads the difference.
///
/// <para><b>Why a difference and not a total.</b> The export is cumulative, and an OpenTelemetry
/// meter provider subscribes to meters by name, so a total can hold other requests' counts. A test
/// that picks tags no other test in the process produces reads exactly its own request.</para>
///
/// <para>The points are summed inside <see cref="BaseExporter{T}.Export"/>, because a
/// <see cref="MetricPoint"/> is only valid while the batch that carries it is being exported.</para>
/// </summary>
public sealed class CountedMetrics
{
    private IReadOnlyList<Point> latest = [];

    /// <summary>Harness options whose gateway exports its metrics to this reader as well.</summary>
    public GatewayHarnessOptions Options(GatewayHarnessOptions? options = null)
    {
        CountedMetrics counted = this;
        options ??= new GatewayHarnessOptions();
        Action<IServiceCollection>? theirs = options.ConfigureServices;
        return options with
        {
            ConfigureServices = services =>
            {
                theirs?.Invoke(services);
                services.ConfigureOpenTelemetryMeterProvider(meters =>
                    meters.AddReader(new BaseExportingMetricReader(new Exporter(counted))));
            },
        };
    }

    /// <summary>
    /// What <paramref name="metric"/> has counted so far under every tag in <paramref name="tags"/>.
    /// A tag expected to be null matches a point that carries it as null or does not carry it.
    /// </summary>
    public long Sum(GatewayHarness harness, string metric, params (string Key, object? Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(harness);

        // Collects now: the reader has no timer, so this is the only time it exports.
        harness.Services.GetRequiredService<MeterProvider>().ForceFlush();

        return Volatile.Read(ref latest)
            .Where(point => point.Metric == metric && tags.All(tag => Matches(point, tag.Key, tag.Value)))
            .Sum(point => point.Value);
    }

    private static bool Matches(Point point, string key, object? expected)
    {
        string? actual = point.Tags.TryGetValue(key, out string? value) ? value : null;
        return actual == expected?.ToString();
    }

    private sealed record Point(string Metric, IReadOnlyDictionary<string, string?> Tags, long Value);

    private sealed class Exporter(CountedMetrics destination) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            List<Point> points = [];
            foreach (Metric metric in batch)
            {
                if (metric.MetricType != MetricType.LongSum)
                {
                    continue;
                }

                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    Dictionary<string, string?> tags = new(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                    {
                        tags[tag.Key] = tag.Value?.ToString();
                    }

                    points.Add(new Point(metric.Name, tags, point.GetSumLong()));
                }
            }

            Volatile.Write(ref destination.latest, points);
            return ExportResult.Success;
        }
    }
}
