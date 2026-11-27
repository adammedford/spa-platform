using System.Security.Cryptography;
using System.Text.Json;
using Gateway.IntegrationTests.Functional;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// One request's own request line, found by the trace id the request carried in. The telemetry stage
/// writes the line in a <c>finally</c> after the pipeline returns, so a response whose body was already
/// sent can reach the test before its line, and the line of the request before it can land after the
/// test has counted. Neither "the last line" nor "one more line than before" is then this request's.
/// A sampled <c>traceparent</c> makes the request activity a child of that context, so its line carries
/// that trace id and no other line does (InboundTraceContextTests pins the seam).
/// </summary>
internal static class TracedLine
{
    /// <summary>
    /// <paramref name="options"/> with a fresh sampled <c>traceparent</c> added (version 00, flags 01:
    /// https://www.w3.org/TR/trace-context/#traceparent-header), and the trace id it names.
    /// </summary>
    public static (string TraceId, RequestOptions Options) Options(RequestOptions? options = null)
    {
        string traceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string traceparent = $"00-{traceId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-01";
        RequestOptions start = options ?? new RequestOptions();
        Dictionary<string, string?> headers = new(start.Headers, StringComparer.OrdinalIgnoreCase) { ["traceparent"] = traceparent };
        return (traceId, start with { Headers = headers });
    }

    /// <summary>Waits for the one request line under <paramref name="traceId"/> and returns it.</summary>
    public static async Task<JsonElement> OfAsync(GatewayHarness harness, string traceId, CancellationToken ct)
    {
        await CapturedTelemetry.UntilAsync(() => Under(harness, traceId).Any(), $"the request line under trace {traceId}", ct);
        return Assert.Single(Under(harness, traceId));
    }

    private static IEnumerable<JsonElement> Under(GatewayHarness harness, string traceId) =>
        CapturedTelemetry.RequestLines(harness).Where(line =>
            line.TryGetProperty("trace_id", out JsonElement id) && id.GetString() == traceId);

}
