using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Telemetry;

/// <summary>
/// Stage 2 (contracts/pipeline.md): the request log line, the app on the request's span, and the
/// <c>gateway.requests</c> count, all written once the rest of the pipeline has returned.
///
/// The app is read after the pipeline because stage 4 pins it later than this stage runs. A request
/// stage 4 refused has no <see cref="IAppFeature"/>, and its line says so with a null <c>app_id</c>.
///
/// <c>rule</c> and <c>session_ref</c> are not on this line yet: stages 6 to 9 have no way to hand
/// them here. Change request <c>contracts/changes/6.4-request-telemetry-feature.md</c> asks for one.
/// Until then each refusal's own line carries its rule id and the trace id that joins it to this one.
/// </summary>
internal sealed class RequestTelemetry
{
    /// <summary>The meter and the activity source every platform instrument lives under.</summary>
    public const string MeterName = "Platform.Telemetry";

    public const string Category = "Platform.Telemetry.Request";

    private readonly ILogger logger;
    private readonly Counter<long> requests;

    public RequestTelemetry(ILoggerFactory loggers, IMeterFactory meters)
    {
        logger = loggers.CreateLogger(Category);
        requests = meters.Create(MeterName).CreateCounter<long>(
            "gateway.requests", unit: "{request}", description: "Requests the gateway answered, by app, route and status.");
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        long started = Stopwatch.GetTimestamp();
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
            Complete(context, Stopwatch.GetElapsedTime(started), failed);
        }
    }

    private void Complete(HttpContext context, TimeSpan elapsed, bool failed)
    {
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

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.Log(LogLevel.Information, new EventId(1, "Request"),
                new RequestLine(appId, revision, route, status, Math.Round(elapsed.TotalMilliseconds, 2)),
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
    private sealed class RequestLine(string? appId, long? revision, string route, int status, double durationMs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] fields =
        [
            new("app_id", appId),
            new("registry_rev", revision),
            new("route", route),
            new("status", status),
            new("duration_ms", durationMs),
        ];

        public int Count => fields.Length;

        public KeyValuePair<string, object?> this[int index] => fields[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => fields.GetEnumerator();

        public override string ToString() => "request";
    }
}
