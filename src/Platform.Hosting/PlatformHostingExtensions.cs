using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Platform.Hosting;

/// <summary>
/// Workstream 2's entry points: stages 3 and 4 of <c>contracts/pipeline.md</c>, and the asset
/// endpoint at stage 10.
///
/// <b>Stubs.</b> Task I-4 wrote these so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Tasks 2.3 and 2.4 fill them in. Keep these signatures and add no others.
/// </summary>
public static class PlatformHostingExtensions
{
    public static IHostApplicationBuilder AddPlatformHosting(this IHostApplicationBuilder builder) => builder;

    /// <summary>
    /// Answers <c>/healthz</c> and <c>/readyz</c> on any host, before host resolution, so the ALB
    /// health check and <c>spactl wait</c> need no registry entry.
    ///
    /// The stub answers 200 on <c>/healthz</c> while the process runs, and 503 on <c>/readyz</c>
    /// because no snapshot is live and none can be until task 2.2 lands. Task 2.3 reports the live
    /// revision there instead.
    /// </summary>
    public static IApplicationBuilder UsePlatformHealth(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/healthz")
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            if (context.Request.Path == "/readyz")
            {
                // No snapshot, so not ready. Task 2.3 answers 200 with {"revision": N} once there is
                // one.
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await next();
        });

    /// <summary>
    /// The request authority must equal a registry <c>host</c> exactly; the app and the snapshot are
    /// pinned for the request as <c>IAppFeature</c>, and any other host gets 404 before routing runs.
    ///
    /// The stub answers 404 to everything, which is the correct behaviour with no registry: every
    /// host is an unknown host. Task 2.3 resolves the known ones.
    /// </summary>
    public static IApplicationBuilder UsePlatformHostResolution(this IApplicationBuilder app) =>
        app.Use((HttpContext context, RequestDelegate _) =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

    /// <summary>
    /// Everything that is not <c>/bff/*</c> or <c>/api/*</c>, served from S3. Task 2.4 fills it in:
    /// the key built from registry values and a validated path, conditional requests passed through,
    /// content type by extension, the cache policy and the fallback rules.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformAssets(this IEndpointRouteBuilder endpoints) => endpoints;
}
