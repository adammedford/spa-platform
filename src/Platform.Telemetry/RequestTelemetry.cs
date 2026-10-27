using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Telemetry;

/// <summary>
/// <c>UsePlatformTelemetry</c> (contracts/pipeline.md): the request log line, the app on the request's span, and the
/// <c>gateway.requests</c>, <c>security.rejections</c> and <c>storage.read_refusals</c> counts, all
/// written once the rest of the pipeline has returned.
///
/// The app is read after the pipeline because host resolution pins it later than this stage runs. A
/// request host resolution rejected, or one refused before it ran, has no <see cref="IAppFeature"/>, and its line says so with a null <c>app_id</c>.
///
/// <c>rule</c> and <c>session_ref</c> are not this stage's to compute (contracts/telemetry.md -> How
/// <c>rule</c> and <c>session_ref</c> reach the request line). This stage sets a fresh
/// <see cref="IRequestTelemetryFeature"/> on the request before calling the rest of the pipeline, a
/// later stage writes it, and this stage reads it back afterwards. A request with a <c>rule</c> is a
/// rejection, and is counted under <c>security.rejections</c> here and nowhere else, so the count
/// equals the request lines that carry a rule. That holds for every rejection the application
/// answers, because this stage runs first (ADR-0018): the method allowlist's 405 in
/// <c>UsePlatformForwardedHeaders</c> carries <c>gate.method</c> on its line and in the count, with a
/// null <c>app_id</c>, like any other.
///
/// <c>read_refused</c> reaches the line the same way: the asset route writes which class of key the
/// store refused to read, and this stage logs it and counts it under <c>storage.read_refusals</c>, the
/// second instrument this stage owns for an event another project raises (contracts/telemetry.md -> The
/// instruments; change request contracts/changes/issue-381-object-read-refusal.md, items 5 and 6).
/// Unlike <c>rule</c> and <c>session_ref</c>, the line carries the field only when a read was refused
/// (#429), so a line for any other request is the one it was before.
/// </summary>
internal sealed class RequestTelemetry
{
    /// <summary>
    /// This project's meter, named <c>Platform.&lt;Project&gt;</c> as contracts/telemetry.md -> The
    /// instruments requires. Other projects' instruments live under their own names.
    /// </summary>
    public const string MeterName = "Platform.Telemetry";

    public const string Category = "Platform.Telemetry.Request";

    private readonly ILogger logger;
    private readonly Counter<long> requests;
    private readonly Counter<long> rejections;
    private readonly Counter<long> readRefusals;

    public RequestTelemetry(ILoggerFactory loggers, IMeterFactory meters)
    {
        logger = loggers.CreateLogger(Category);
        Meter meter = meters.Create(MeterName);
        requests = meter.CreateCounter<long>(
            "gateway.requests", unit: "{request}", description: "Requests the gateway answered, by app, route and status.");
        rejections = meter.CreateCounter<long>(
            "security.rejections", unit: "{request}", description: "Requests the gateway rejected, by rule id and app.");
        readRefusals = meter.CreateCounter<long>(
            "storage.read_refusals", unit: "{request}", description: "Requests whose object read the store refused, by app and key class.");
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        long started = Stopwatch.GetTimestamp();

        // A fresh feature per request: whatever a later stage leaves here is this request's alone.
        RequestTelemetryFeature fields = new();
        context.Features.Set<IRequestTelemetryFeature>(fields);

        bool failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Complete(context, fields, Stopwatch.GetElapsedTime(started), failed);
        }
    }

    private void Complete(HttpContext context, RequestTelemetryFeature fields, TimeSpan elapsed, bool failed)
    {
        string? rule = fields.Rule;
        string? readRefused = fields.ReadRefused;
        IAppFeature? resolved = context.Features.Get<IAppFeature>();
        string? appId = resolved?.App.AppId;
        long? revision = resolved?.Snapshot.Revision;
        string route = RouteOf(context.Request.Path);

        // An exception the response had not started for becomes a 500 at the server, after this.
        int status = failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;

        // F-11: the span names the app, so a trace found by id says whose request it was.
        Activity? span = context.Features.Get<IHttpActivityFeature>()?.Activity;
        if (span is not null)
        {
            span.SetTag("app_id", appId);
            span.SetTag("route", route);
        }

        requests.Add(1,
            new KeyValuePair<string, object?>("app_id", appId),
            new KeyValuePair<string, object?>("route", route),
            new KeyValuePair<string, object?>("status", status));

        // contracts/telemetry.md -> The instruments: this stage is the one owner of this count.
        if (rule is not null)
        {
            rejections.Add(1,
                new KeyValuePair<string, object?>("rule", rule),
                new KeyValuePair<string, object?>("app_id", appId));
        }

        // contracts/telemetry.md -> The instruments: from the field the line logs, and before the line's
        // level check, so the count and the lines carrying read_refused are one decision per request.
        if (readRefused is not null)
        {
            readRefusals.Add(1,
                new KeyValuePair<string, object?>("app_id", appId),
                new KeyValuePair<string, object?>("key_class", readRefused));
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.Log(LogLevel.Information, new EventId(1, "Request"),
                new RequestLine(appId, revision, route, fields, status, Math.Round(elapsed.TotalMilliseconds, 2)),
                exception: null, static (_, _) => "request");
        }
    }

    /// <summary>The <c>route</c> values in contracts/telemetry.md, by the paths in the browser contract.</summary>
    internal static string RouteOf(PathString path)
    {
        if (path.StartsWithSegments("/api"))
        {
            return "api";
        }

        if (path.StartsWithSegments("/bff"))
        {
            return "bff";
        }

        if (path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/signout-callback-oidc")
            || path.StartsWithSegments("/signout-oidc"))
        {
            return "callback";
        }

        if (path.StartsWithSegments("/healthz") || path.StartsWithSegments("/readyz"))
        {
            return "health";
        }

        return "asset";
    }

    /// <summary>
    /// The line's fields, as structured state the formatter writes field by field. <c>read_refused</c>
    /// sits beside <c>rule</c> and <c>session_ref</c>, the feature's other two fields, and only on a line
    /// whose read was refused: a null is left out rather than written, as #429 asks.
    /// </summary>
    private sealed class RequestLine(
        string? appId, long? revision, string route, IRequestTelemetryFeature telemetry, int status, double durationMs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] fields =
        [
            new("app_id", appId),
            new("registry_rev", revision),
            new("route", route),
            new("rule", telemetry.Rule),
            new("session_ref", telemetry.SessionRef),
            .. telemetry.ReadRefused is { } refused ? [new KeyValuePair<string, object?>("read_refused", refused)] : (KeyValuePair<string, object?>[])[],
            new("status", status),
            new("duration_ms", durationMs),
        ];

        public int Count => fields.Length;

        public KeyValuePair<string, object?> this[int index] => fields[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => fields.GetEnumerator();

        public override string ToString() => "request";
    }

    /// <summary>
    /// The feature this stage hands the rest of the pipeline. Every field starts null, which is what a
    /// request that no stage rejected, that carried no session and whose object read was not refused keeps.
    /// </summary>
    private sealed class RequestTelemetryFeature : IRequestTelemetryFeature
    {
        public string? Rule { get; set; }

        public string? SessionRef { get; set; }

        public string? ReadRefused { get; set; }
    }
}
