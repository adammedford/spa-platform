using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// Workstream 2's entry points: stages 3 and 4 of <c>contracts/pipeline.md</c>, and the asset
/// endpoint at stage 10. Keep these signatures and add no others.
///
/// Task 2.3 filled stages 3 and 4; task 2.4 filled the asset endpoint.
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
    /// Stage 10's third endpoint: everything that is not <c>/bff/*</c> or <c>/api/*</c>, served from
    /// the app's live release in S3 (plan row 2.4; contracts/s3-layout.md). The key is built from
    /// registry values and a validated path, <c>If-None-Match</c> is passed through, the content type
    /// comes from the extension, the cache policy from the path, an extensionless path that accepts
    /// HTML gets <c>index.html</c>, and the platform's reserved paths are never read.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformAssets(this IEndpointRouteBuilder endpoints)
    {
        // Resolved once, when the endpoint is mapped, as UsePlatformHealth resolves the registry: a
        // composition without an object store fails at start-up, not on the first asset. In the gateway
        // AddPlatformRegistry registers it, one store for the registry and every release (task 2.2).
        Assets assets = new(
            endpoints.ServiceProvider.GetRequiredService<IObjectStore>(),
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Hosting.Assets"));

        // A catch-all with every method, ordered last. Not the parameterless MapFallback: its pattern is
        // {*path:nonfile}, which never matches a path with an extension, so /app.js would miss
        // (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.endpointroutebuilderextensions.mapfallback).
        // Every method, so a POST to an asset path gets the contract's 405 problem rather than routing's
        // empty one. Order int.MaxValue, as MapFallback sets it, so /bff and /api endpoints always win.
        endpoints.MapFallback("/{**path}", assets.InvokeAsync);

        return endpoints;
    }
}
