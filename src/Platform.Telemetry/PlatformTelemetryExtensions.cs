using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Platform.Telemetry;

/// <summary>
/// Workstream 6's entry points, stage 2 of <c>contracts/pipeline.md</c>.
///
/// <b>Stubs.</b> Task I-4 wrote these so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Task 6.4 fills them in: JSON logs with redaction, OpenTelemetry traces and metrics,
/// authentication loggers capped at Information, and the log canary test (N-16, F-11). Keep these
/// signatures and add no others.
/// </summary>
public static class PlatformTelemetryExtensions
{
    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder) => builder;

    /// <summary>
    /// The request log line and the trace scope. Task 6.4 replaces the framework's default request
    /// log, which includes the query string.
    /// </summary>
    public static IApplicationBuilder UsePlatformTelemetry(this IApplicationBuilder app) => app;
}
