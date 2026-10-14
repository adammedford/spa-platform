using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// Workstream 2's entry points: stages 3 and 4 of <c>contracts/pipeline.md</c>, and the asset
/// endpoint at stage 10. Keep these signatures and add no others.
///
/// Task 2.3 filled stages 3 and 4. <see cref="MapPlatformAssets"/> is still task I-4's stub, and task
/// 2.4 fills it in.
/// </summary>
public static class PlatformHostingExtensions
{
    /// <summary>
    /// Registers nothing yet. Stages 3 and 4 need only <see cref="IRegistry"/>, which
    /// <c>AddPlatformRegistry</c> registers before this runs (contracts/pipeline.md, Service
    /// registration).
    /// </summary>
    public static IHostApplicationBuilder AddPlatformHosting(this IHostApplicationBuilder builder) => builder;

    /// <summary>
    /// Stage 3. Answers <c>/healthz</c> and <c>/readyz</c> on any host, before host resolution, so the
    /// ALB health check and <c>spactl wait</c> need no registry entry and no known host
    /// (contracts/pipeline.md, Health endpoints, and the second of its three orderings).
    /// </summary>
    public static IApplicationBuilder UsePlatformHealth(this IApplicationBuilder app)
    {
        // Resolved once, when the pipeline is built: the gateway has one registry for its lifetime, and
        // a composition that forgot AddPlatformRegistry fails at start-up rather than on the first probe.
        IRegistry registry = app.ApplicationServices.GetRequiredService<IRegistry>();
        return app.Use((context, next) => Health.TryAnswerAsync(context, registry) ?? next(context));
    }

    /// <summary>
    /// Stage 4. The request authority must equal a registry <c>host</c> exactly; the app and the
    /// snapshot are pinned for the request as <see cref="IAppFeature"/>, and any other host gets 404
    /// before routing runs (contracts/pipeline.md; N-08, S-A3).
    /// </summary>
    public static IApplicationBuilder UsePlatformHostResolution(this IApplicationBuilder app)
    {
        IRegistry registry = app.ApplicationServices.GetRequiredService<IRegistry>();
        return app.Use((context, next) => HostResolution.InvokeAsync(context, next, registry));
    }

    /// <summary>
    /// Everything that is not <c>/bff/*</c> or <c>/api/*</c>, served from S3. Task 2.4 fills it in:
    /// the key built from registry values and a validated path, conditional requests passed through,
    /// content type by extension, the cache policy and the fallback rules.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformAssets(this IEndpointRouteBuilder endpoints) => endpoints;
}
