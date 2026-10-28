using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// The inbound trace context the deployed continuity class places its requests by (#358 Q143): a
/// request that arrives with a sampled W3C <c>traceparent</c> is logged on a request line whose
/// <c>trace_id</c> is that header's trace id, for an anonymous request and for a session's. The
/// framework's hosting diagnostics start the request's activity as a child of the inbound context
/// (https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-concepts), and
/// <c>UsePlatformTelemetry</c> writes the activity's trace id (contracts/telemetry.md), so this pins
/// that seam without a contract change: a client trace id finds its own request line, and no other.
/// </summary>
public sealed class InboundTraceContextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Version 00, a random trace id and parent id, flags 01 (sampled): https://www.w3.org/TR/trace-context/#traceparent-header.</summary>
    private static (string TraceId, string Header) Sampled()
    {
        string traceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return (traceId, $"00-{traceId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-01");
    }

    [Fact]
    public async Task An_anonymous_request_with_a_sampled_traceparent_is_logged_under_its_trace_id()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        (string traceId, string traceparent) = Sampled();

        await browser.NavigateAsync(harness.App("app1").Url("/healthz"), ct: Ct);
        BrowserResponse response = await browser.GetAsync("/bff/session", RequestOptions.With(("traceparent", traceparent)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement line = await LineAsync(harness, traceId);
        Assert.Equal("bff", line.GetProperty("route").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("session_ref").ValueKind);
    }

    [Fact]
    public async Task A_session_request_with_a_sampled_traceparent_is_logged_under_its_trace_id()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Ct);
        (string traceId, string traceparent) = Sampled();

        BrowserResponse response = await browser.GetAsync(harness.App("app1").Url("/bff/session").ToString(), RequestOptions.With(("traceparent", traceparent)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement line = await LineAsync(harness, traceId);
        Assert.Equal("bff", line.GetProperty("route").GetString());
        Assert.Matches("^[0-9a-f]{12}$", line.GetProperty("session_ref").GetString());
    }

    [Fact]
    public async Task Without_a_traceparent_the_request_line_has_a_trace_id_of_its_own()
    {
        // Two requests with no traceparent each get a well-formed trace id, and not the same one: the
        // trace id on a request line is the request's own, so a line found by a client's trace id is
        // found by the header and never because every line shares a trace id or carries a stale one.
        // Mutation: every request line written under one fixed trace id; the two lines are no longer
        // distinct. A second: no activity started, so trace_id is null; the shape check fails.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();
        (string unsent, _) = Sampled();

        await browser.NavigateAsync(harness.App("app1").Url("/healthz"), ct: Ct);
        await browser.GetAsync("/bff/session", ct: Ct);
        await browser.GetAsync("/bff/session", ct: Ct);

        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Count(line => line.GetProperty("route").GetString() == "bff") >= 2,
            "two bff request lines", Ct);
        List<string?> traceIds = CapturedTelemetry.RequestLines(harness)
            .Where(line => line.GetProperty("route").GetString() == "bff")
            .Select(line => line.GetProperty("trace_id").GetString())
            .ToList();

        Assert.Equal(2, traceIds.Count);
        Assert.All(traceIds, traceId => Assert.Matches("^[0-9a-f]{32}$", traceId));
        Assert.NotEqual(traceIds[0], traceIds[1]);
        Assert.DoesNotContain(unsent, traceIds);
    }

    /// <summary>The one request line carrying the trace id; the line is written after the response, so it is waited for.</summary>
    private static async Task<JsonElement> LineAsync(GatewayHarness harness, string traceId)
    {
        await CapturedTelemetry.UntilAsync(
            () => CapturedTelemetry.RequestLines(harness).Any(line => line.GetProperty("trace_id").GetString() == traceId),
            $"a request line with trace id {traceId}", Ct);
        return Assert.Single(CapturedTelemetry.RequestLines(harness), line => line.GetProperty("trace_id").GetString() == traceId);
    }
}
