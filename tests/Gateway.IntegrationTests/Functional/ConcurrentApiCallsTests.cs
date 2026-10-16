using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Platform.Testing;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-11's control: a trace id identifies a request, not the process. Two calls are made at once and
/// the ids they carry to the upstream are compared. Without this, every assertion in
/// <see cref="ApiTelemetryTests"/> would also hold if the gateway minted one id per process and
/// stamped it on everything. Task 6.2a.
/// </summary>
public sealed class ConcurrentApiCallsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_calls_carry_two_trace_ids()
    {
        CapturedTelemetry telemetry = new();
        await using GatewayHarness harness = await GatewayHarness.StartAsync(telemetry.Options(new GatewayHarnessOptions { SessionRequired = true }), Ct);

        using TestBrowser one = harness.CreateBrowser();
        using TestBrowser two = harness.CreateBrowser();
        await harness.SignInAsync(one, "app1", Ct);
        await harness.SignInAsync(two, "app1", Ct);

        // Two tabs of the same app calling the same API at the same time, which is the case an
        // operator reads a trace for.
        BrowserResponse[] responses = await Task.WhenAll(
            one.GetAsync(harness.App("app1").Url("/api/echo/items/first").ToString(), ct: Ct),
            two.GetAsync(harness.App("app1").Url("/api/echo/items/second").ToString(), ct: Ct));

        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Count(line => line.GetProperty("route").GetString() == "api") == 2,
            "both API calls' request log lines", Ct);

        string first = TraceIdOf(harness, "/items/first");
        string second = TraceIdOf(harness, "/items/second");
        string[] logged = [.. CapturedTelemetry.RequestLines(harness)
            .Where(line => line.GetProperty("route").GetString() == "api")
            .Select(line => line.GetProperty("trace_id").GetString()!)];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.NotEqual(first, second);

        // Ordered on both sides: the two calls race, so which request line the telemetry stage wrote first is
        // not this row's claim.
        Assert.Equal(new[] { first, second }.Order(), logged.Order());

        // Each call's hop hangs off its own request, so neither trace borrowed the other's spans.
        foreach (string traceId in (string[])[first, second])
        {
            JsonElement line = Assert.Single(CapturedTelemetry.RequestLines(harness),
                candidate => candidate.GetProperty("trace_id").GetString() == traceId);
            Assert.Equal(
                line.GetProperty("span_id").GetString(),
                Assert.Single(telemetry.Trace(traceId), span => span.Source.Name == "Yarp.ReverseProxy")
                    .ParentSpanId.ToHexString());
        }
    }

    /// <summary>The trace id the upstream was told, for the call that reached <paramref name="path"/>.</summary>
    private static string TraceIdOf(GatewayHarness harness, string path)
    {
        EchoedRequest received = Assert.Single(harness.Echo.Requests, request => request.Path == path);
        return received.Headers["traceparent"].Single().Split('-')[1];
    }
}
