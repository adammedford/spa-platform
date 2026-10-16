using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// What one harness's gateway has counted on its own counters. It answers one question, "how much
/// has this counter counted under these tags", and a test asks it before and after its request and
/// reads the difference.
///
/// <para><b>Why a listener scoped to the gateway's meter factory, and not the in-memory exporter the
/// functional suite reads</b> (<c>tests/Gateway.IntegrationTests/Functional/CapturedTelemetry.cs</c>).
/// An OpenTelemetry meter provider subscribes to meters by name, process-wide, and when several
/// harnesses are alive each has its own <c>Platform.Telemetry</c> meter under that one name. Watched
/// with the whole of this suite running, a reader added to this harness's provider exported another
/// harness's <c>security.rejections</c> (its <c>host.unknown</c> and <c>path.invalid</c> counts) and
/// none of this harness's. So a count read there cannot be attributed to a gateway. Every
/// <c>Platform.*</c> meter is created through the gateway's <see cref="IMeterFactory"/>, which sets
/// itself as the meter's <see cref="Meter.Scope"/>, and that is the one property that tells two
/// harnesses' instruments apart.</para>
///
/// <para>Every measurement is kept as it is recorded, which is synchronous with the instrument's
/// <c>Add</c>, so whatever a stage counted before it wrote its log line is here once the line is.</para>
/// </summary>
public sealed class CountedMetrics : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentQueue<Measurement> measurements = new();

    private CountedMetrics(IMeterFactory scope)
    {
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, scope))
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Dictionary<string, string?> named = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                named[tag.Key] = tag.Value?.ToString();
            }

            measurements.Enqueue(new Measurement(instrument.Name, named, value));
        });
        listener.Start();
    }

    /// <summary>Starts counting what <paramref name="harness"/>'s gateway records from now on.</summary>
    public static CountedMetrics Watch(GatewayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return new CountedMetrics(harness.Services.GetRequiredService<IMeterFactory>());
    }

    /// <summary>
    /// What <paramref name="metric"/> has counted since <see cref="Watch"/> under every tag in
    /// <paramref name="tags"/>. A tag expected to be null matches a measurement that carries it as
    /// null or does not carry it.
    /// </summary>
    public long Sum(string metric, params (string Key, object? Value)[] tags) =>
        measurements
            .Where(measurement => measurement.Metric == metric
                && tags.All(tag => Matches(measurement, tag.Key, tag.Value)))
            .Sum(measurement => measurement.Value);

    public void Dispose() => listener.Dispose();

    private static bool Matches(Measurement measurement, string key, object? expected)
    {
        string? actual = measurement.Tags.TryGetValue(key, out string? value) ? value : null;
        return actual == expected?.ToString();
    }

    private sealed record Measurement(string Metric, IReadOnlyDictionary<string, string?> Tags, long Value);
}
