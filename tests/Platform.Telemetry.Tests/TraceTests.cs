using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry.Metrics;

namespace Platform.Telemetry.Tests;

/// <summary>
/// F-11, the gateway's side: the request's log line, its server span and the <c>traceparent</c> the
/// upstream received share one trace id, and the line and the span name the app. Task 6.2 follows
/// the same call through the composed gateway to the mock API.
/// </summary>
public sealed class TraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Activity>> SpansOf(TelemetryHost host, Func<Activity, bool> match)
    {
        // The server span ends after the response has been written, so it can arrive after the
        // client has its answer.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            host.Flush();
            List<Activity> found = [.. host.Spans.Where(match)];
            if (found.Count > 0)
            {
                return found;
            }

            await Task.Delay(20, Ct);
        }

        return [];
    }

    [Fact]
    public async Task F11_the_log_line_the_spans_and_the_upstream_share_one_trace_id_and_name_the_app()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        await client.GetAsync(new Uri(host.BaseAddress, "/api/orders/items"), Ct);

        JsonElement line = Assert.Single(host.Log.Json, l => l.GetProperty("msg").GetString() == "request");
        string traceId = line.GetProperty("trace_id").GetString()!;
        Assert.Equal("app1", line.GetProperty("app_id").GetString());

        // The framework's activity sources are process-wide, so the exporter also sees the test
        // client's own span, the upstream listener's server span and other tests' hosts. The ids are
        // what pick out this call: the gateway's server span is the one the line names, and the
        // upstream call is its child.
        string spanId = line.GetProperty("span_id").GetString()!;
        Activity outbound = Assert.Single(await SpansOf(host, s =>
            s.Kind == ActivityKind.Client && s.ParentSpanId.ToHexString() == spanId));
        Assert.Equal(traceId, outbound.TraceId.ToHexString());
        Activity server = Assert.Single(await SpansOf(host, s => s.SpanId.ToHexString() == spanId));
        Assert.Equal(ActivityKind.Server, server.Kind);
        Assert.Equal(traceId, server.TraceId.ToHexString());
        Assert.Equal("app1", server.GetTagItem("app_id"));

        // W3C trace context, carried to the upstream (contracts/telemetry.md).
        string traceparent = Assert.Single(Assert.Single(host.Upstream.Requests).Headers["traceparent"]);
        Assert.Equal(traceId, traceparent.Split('-')[1]);
    }

    [Fact]
    public async Task Gateway_requests_are_counted_by_app_route_and_status()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        // A status no other test answers with: the meter listener is process-wide, so points from
        // other hosts running in parallel can share this exporter.
        await client.GetAsync(new Uri(host.BaseAddress, "/status/299"), Ct);
        await client.GetAsync(new Uri(host.BaseAddress, "/status/299"), Ct);
        host.Flush();

        List<(long Count, Dictionary<string, object?> Tags)> points = [];
        foreach (Metric requests in host.Metrics.Where(m => m.Name == "gateway.requests" && m.MeterName == "Platform.Telemetry"))
        foreach (ref readonly MetricPoint point in requests.GetMetricPoints())
        {
            Dictionary<string, object?> tags = [];
            foreach (KeyValuePair<string, object?> tag in point.Tags)
            {
                tags[tag.Key] = tag.Value;
            }

            points.Add((point.GetSumLong(), tags));
        }

        (long count, Dictionary<string, object?> found) = Assert.Single(points, p => Equals(p.Tags["status"], 299));
        Assert.Equal(2, count);
        Assert.Equal("app1", found["app_id"]);
        Assert.Equal("asset", found["route"]);
    }
}
