using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.TestHarness;
using OpenTelemetry.Metrics;
using Platform.Testing;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-11, "Follow one API call through the telemetry": "The gateway's log line, its spans and the
/// <c>traceparent</c> the mock API received share one trace id, and the line names the app"
/// (docs/plan/04-tests.md). Task 6.2a. The Tests-tab entry names no gate dependency, and none is
/// needed: the proxy and the telemetry stage are both landed.
///
/// <para>One API call is made once, by <see cref="Call"/>, and each test below asks one question
/// about it. That is the row's own wording — "follow <b>one</b> API call" — and it keeps the request
/// log line unambiguous, because the line carries no path to tell two calls apart by.</para>
///
/// <para>These requests isolate the HTTP contract named above. Session establishment and
/// preservation require separate acceptance evidence; a successful response here proves only the
/// asserted routing, policy or telemetry behaviour.</para>
/// </summary>
public sealed partial class ApiTelemetryTests(ApiTelemetryTests.Call call) : IClassFixture<ApiTelemetryTests.Call>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The API this row follows: the echo upstream, which records what reached it.</summary>
    public const string ApiPath = "/api/echo/items";

    /// <summary>One harness, one API call, and everything the call produced.</summary>
    public sealed class Call : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public CapturedTelemetry Telemetry { get; } = new();

        /// <summary>The one request log line the telemetry stage wrote for the call.</summary>
        public JsonElement Line { get; private set; }

        /// <summary>What the upstream received.</summary>
        public EchoedRequest Upstream { get; private set; } = null!;

        public string TraceId => Line.GetProperty("trace_id").GetString()!;

        public string SpanId => Line.GetProperty("span_id").GetString()!;

        /// <summary>The request's own span: the one the line was written inside.</summary>
        public Activity RequestSpan => Assert.Single(Telemetry.Trace(TraceId), span => span.SpanId.ToHexString() == SpanId);

        /// <summary>The span the proxy emitted for the hop (contracts/telemetry.md -> The instruments).</summary>
        public Activity ProxySpan => Assert.Single(Telemetry.Trace(TraceId), span => span.Source.Name == "Yarp.ReverseProxy");

        public async ValueTask InitializeAsync()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Harness = await GatewayHarness.StartAsync(Telemetry.Options(), ct);

            using TestBrowser browser = Harness.CreateBrowser();
            await Harness.SignInAsync(browser, "app1", ct);
            BrowserResponse response = await browser.GetAsync(Harness.App("app1").Url(ApiPath).ToString(), ct: ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            // The telemetry stage writes the line after the pipeline has returned, and the server span stops after
            // that, so the response arriving does not mean either is here yet.
            await CapturedTelemetry.UntilAsync(
                () => CapturedTelemetry.RequestLines(Harness).Any(line => line.GetProperty("route").GetString() == "api"),
                "the API call's request log line", ct);
            Line = Assert.Single(CapturedTelemetry.RequestLines(Harness), line => line.GetProperty("route").GetString() == "api");

            string traceId = TraceId;
            await CapturedTelemetry.UntilAsync(
                () => Telemetry.Trace(traceId).Any(span => span.Source.Name == "Yarp.ReverseProxy")
                    && Telemetry.Trace(traceId).Any(span => span.SpanId.ToHexString() == SpanId),
                "the API call's spans", ct);

            Telemetry.Flush(Harness);
            Upstream = Assert.Single(Harness.Echo.Requests);
        }

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private Call TheCall => call;

    [Fact]
    public void The_log_line_the_spans_and_the_upstreams_traceparent_carry_one_trace_id()
    {
        string traceparent = TheCall.Upstream.Headers["traceparent"].Single();

        // W3C trace context, version 00: "00-<trace-id>-<parent-id>-<flags>"
        // (https://www.w3.org/TR/trace-context/#traceparent-header-field-values). The trace id is the
        // second field, and it is the one value an operator follows across the hop.
        Match parsed = Traceparent().Match(traceparent);
        Assert.True(parsed.Success, $"the upstream received a traceparent that is not W3C: {traceparent}");

        Assert.Equal(TheCall.TraceId, parsed.Groups["trace"].Value);
        Assert.Equal(TheCall.TraceId, TheCall.RequestSpan.TraceId.ToHexString());
        Assert.Equal(TheCall.TraceId, TheCall.ProxySpan.TraceId.ToHexString());
    }

    [Fact]
    public void The_upstream_joins_the_trace_through_w3c_context_and_no_vendor_header()
    {
        // An upstream the platform does not own joins on the standard header. A vendor header would
        // tie the trace to one backend's agent, which is what contracts/telemetry.md rules out with
        // "Traces carry W3C context to upstreams".
        Assert.Contains("traceparent", TheCall.Upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string vendor in (string[])["b3", "X-B3-TraceId", "uber-trace-id", "X-Amzn-Trace-Id", "X-Cloud-Trace-Context"])
        {
            Assert.DoesNotContain(vendor, TheCall.Upstream.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_proxys_span_is_a_child_of_the_requests_span()
    {
        // The hop is visible as a hop: the forwarder's span hangs off the request's, so a trace found
        // by id shows the gateway answering and the upstream being called inside it.
        Assert.Equal(TheCall.SpanId, TheCall.ProxySpan.ParentSpanId.ToHexString());
        Assert.Equal(TheCall.RequestSpan.SpanId, TheCall.ProxySpan.ParentSpanId);
        Assert.NotEqual(TheCall.SpanId, TheCall.ProxySpan.SpanId.ToHexString());
    }

    [Fact]
    public void The_request_line_names_the_app_and_the_span_carries_it_too()
    {
        // "and the line names the app": a trace in a multi-tenant log is attributable without a
        // second lookup. The telemetry stage puts the same app on the span (contracts/telemetry.md).
        Assert.Equal("app1", TheCall.Line.GetProperty("app_id").GetString());
        Assert.Equal("app1", TheCall.RequestSpan.GetTagItem("app_id"));
        Assert.Equal("api", TheCall.RequestSpan.GetTagItem("route"));
        Assert.Equal(TheCall.Harness.Registry.Current.Revision, TheCall.Line.GetProperty("registry_rev").GetInt64());
    }

    [Fact]
    public void The_request_lines_fields_are_the_telemetry_contracts_list_in_its_order()
    {
        // contracts/telemetry.md -> Log fields, read from the contract rather than copied here.
        // "category" is the formatter's own field, beside the contract's list rather than one of it
        // (src/Platform.Telemetry/PlatformJsonFormatter.cs, task 6.4, asserted in that project's
        // tests); the contract's closed list is the "Never logged" row, which N-16 holds.
        IEnumerable<string> written = TheCall.Line.EnumerateObject()
            .Select(field => field.Name)
            .Where(name => name != "category");

        // read_refused is the one field a line carries only when it has a value: the asset route's
        // refused object read (contracts/telemetry.md; #429), and an API call reads no object. So the
        // expected list is the row without it, and a line that wrote it as null fails here.
        Assert.Equal(Contract.LogFields.Where(name => name != "read_refused"), written);
        Assert.Contains(TheCall.Line.GetProperty("route").GetString(), Contract.RouteValues);
        Assert.Equal("information", TheCall.Line.GetProperty("level").GetString());
        Assert.Equal(200, TheCall.Line.GetProperty("status").GetInt32());

        // The call is made in a session, so the line carries the session's reference: twelve hex
        // characters (contracts/telemetry.md), the same reference the login's callback line carries.
        string? sessionRef = TheCall.Line.GetProperty("session_ref").GetString();
        Assert.Matches("^[0-9a-f]{12}$", sessionRef);
        Assert.Equal(sessionRef, Assert.Single(CapturedTelemetry.RequestLines(TheCall.Harness),
            line => line.GetProperty("route").GetString() == "callback").GetProperty("session_ref").GetString());

        // Not a rejection either, so no rule id: an upstream's own 4xx and this 200 both leave it null.
        Assert.Equal(JsonValueKind.Null, TheCall.Line.GetProperty("rule").ValueKind);
    }

    [Fact]
    public void Every_platform_instrument_is_named_for_a_project_and_the_proxy_keeps_its_own_source()
    {
        // contracts/telemetry.md -> The instruments: every platform Meter and ActivitySource is
        // "Platform.<Project>", and workstream 6 subscribes to Platform.* plus the proxy's own
        // source. So an instrument under the platform's prefix that names no project is one nobody
        // sees, and the only way to notice is to read the names that did arrive.
        IEnumerable<string> platformSpans = TheCall.Telemetry.Spans
            .Select(span => span.Source.Name)
            .Where(name => name.StartsWith("Platform.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        IEnumerable<string> platformMeters = TheCall.Telemetry.Metrics
            .Select(metric => metric.MeterName)
            .Where(name => name.StartsWith("Platform.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

        Assert.All(platformSpans, name => Assert.Contains(name, FixtureFiles.PlatformInstrumentNames));
        Assert.All(platformMeters, name => Assert.Contains(name, FixtureFiles.PlatformInstrumentNames));

        // CODING_STANDARDS.md -> "An assertion must be able to fail", second shape: the span line
        // above is vacuous today. Nothing in the tree creates an ActivitySource, so every span here
        // comes from the framework's instrumentation or from the proxy and none carries the
        // platform's prefix; the line starts biting when a workstream adds one. The meter line is
        // the half with data, and the mechanism both rest on is checked against an input that breaks
        // it: a name under the platform's prefix that is no project under src/ is not accepted.
        Assert.DoesNotContain("Platform.NotAProject", FixtureFiles.PlatformInstrumentNames);

        // And the subscription is not vacuous: the platform's own counter did arrive, under a name the
        // contract's metric list holds, and the proxy's span came from the source named beside them.
        // Several harnesses can be alive at once and each has its own meter, so the count is not
        // asserted: that the counter arrived at all, and arrived from a platform project, is.
        IEnumerable<Metric> requests = TheCall.Telemetry.Metrics.Where(metric => metric.Name == "gateway.requests");
        Assert.NotEmpty(requests);
        Assert.Contains("gateway.requests", Contract.MetricNames);
        Assert.All(requests, metric => Assert.Contains(metric.MeterName, FixtureFiles.PlatformInstrumentNames));
        Assert.Equal("Yarp.ReverseProxy", TheCall.ProxySpan.Source.Name);
    }

    [GeneratedRegex(@"^00-(?<trace>[0-9a-f]{32})-(?<parent>[0-9a-f]{16})-(?<flags>[0-9a-f]{2})$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Traceparent();
}
