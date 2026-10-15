using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Telemetry;

/// <summary>
/// Stage 2 (contracts/pipeline.md): the request log line, the app on the request's span, and the
/// <c>gateway.requests</c> and <c>security.rejections</c> counts, all written once the rest of the
/// pipeline has returned.
///
/// The app is read after the pipeline because stage 4 pins it later than this stage runs. A request
/// stage 4 rejected has no <see cref="IAppFeature"/>, and its line says so with a null <c>app_id</c>.
///
/// <c>rule</c> and <c>session_ref</c> are not this stage's to compute (contracts/telemetry.md -> How
/// <c>rule</c> and <c>session_ref</c> reach the request line). This stage sets a fresh
/// <see cref="IRequestTelemetryFeature"/> on the request before calling the rest of the pipeline, a
/// later stage writes it, and this stage reads it back afterwards. A request with a <c>rule</c> is a
/// rejection, and is counted under <c>security.rejections</c> here and nowhere else, so the count
/// equals the request lines that carry a rule. The stage-1 method allowlist answers before this
/// stage runs; its 405 is outside both, as contracts/telemetry.md records.
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

    public RequestTelemetry(ILoggerFactory loggers, IMeterFactory meters)
    {
        logger = loggers.CreateLogger(Category);
        Meter meter = meters.Create(MeterName);
        requests = meter.CreateCounter<long>(
            "gateway.requests", unit: "{request}", description: "Requests the gateway answered, by app, route and status.");
        rejections = meter.CreateCounter<long>(
            "security.rejections", unit: "{request}", description: "Requests the gateway rejected, by rule id and app.");
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

        // contracts/telemetry.md -> The instruments: stage 2 is the one owner of this count.
        if (rule is not null)
        {
            rejections.Add(1,
                new KeyValuePair<string, object?>("rule", rule),
                new KeyValuePair<string, object?>("app_id", appId));
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

    /// <summary>The line's fields, as structured state the formatter writes field by field.</summary>
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
    /// The feature this stage hands the rest of the pipeline. Both fields start null, which is what a
    /// request that no stage rejected and that carried no session keeps.
    /// </summary>
    private sealed class RequestTelemetryFeature : IRequestTelemetryFeature
    {
        public string? Rule { get; set; }

        public string? SessionRef { get; set; }
    }
}
