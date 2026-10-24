using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// Workstream 2's entry points in <c>contracts/pipeline.md</c>: <c>UsePlatformHealth</c> then
/// <c>UsePlatformHostResolution</c>, and the asset endpoint, <c>MapPlatformAssets</c>. Keep these
/// signatures and add no others.
///
/// Task 2.3 filled <c>UsePlatformHealth</c> and <c>UsePlatformHostResolution</c>; task 2.4 filled
/// the asset endpoint.
/// </summary>
public static class PlatformHostingExtensions
{
    /// <summary>
    /// Registers the <see cref="GatewayInstance"/> <c>/readyz</c> names, unless one is registered
    /// already, which is how a harness gives a host a deterministic id (change request P2-5, decision
    /// 2). Health and host resolution otherwise need only <see cref="IRegistry"/>, which
    /// <c>AddPlatformRegistry</c> registers before this runs (contracts/pipeline.md, Service
    /// registration).
    ///
    /// Also registers the cell's key ring when a <c>DataProtection</c> section is present, and refuses a
    /// configuration the ring cannot honour (plan row P2-2; ADR-0025; see <see cref="KeyRing"/>).
    /// </summary>
    public static IHostApplicationBuilder AddPlatformHosting(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        KeyRing.Register(builder);

        // Read from the process environment, not configuration: the ECS agent sets the variable, and
        // P2-5 (decision 2) rules out a configuration key for the instance.
        builder.Services.TryAddSingleton(services => GatewayInstance.Start(
            Environment.GetEnvironmentVariable(GatewayInstance.MetadataVariable),
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Hosting.Instance")));
        return builder;
    }

    /// <summary>
    /// Answers <c>/healthz</c> and <c>/readyz</c> on any host, before host resolution, so the
    /// ALB health check and <c>spactl wait</c> need no registry entry and no known host
    /// (contracts/pipeline.md, Health endpoints, and the ordering "Health answers before host
    /// resolution").
    /// </summary>
    public static IApplicationBuilder UsePlatformHealth(this IApplicationBuilder app)
    {
        // Resolved once, when the pipeline is built: the gateway has one registry for its lifetime, and
        // a composition that forgot AddPlatformRegistry fails at start-up rather than on the first probe.
        IRegistry registry = app.ApplicationServices.GetRequiredService<IRegistry>();

        // Resolved here too, so the instance's one metadata read starts with the pipeline, not on the
        // first probe (change request P2-5, decision 2: read once at start-up).
        GatewayInstance instance = app.ApplicationServices.GetRequiredService<GatewayInstance>();
        return app.Use((context, next) => Health.TryAnswerAsync(context, registry, instance) ?? next(context));
    }

    /// <summary>
    /// The request authority must equal a registry <c>host</c> exactly; the app and the snapshot
    /// are pinned for the request as <see cref="IAppFeature"/>, and any other host gets 404 before
    /// routing runs (contracts/pipeline.md; N-08, S-A3).
    /// </summary>
    public static IApplicationBuilder UsePlatformHostResolution(this IApplicationBuilder app)
    {
        IRegistry registry = app.ApplicationServices.GetRequiredService<IRegistry>();
        return app.Use((context, next) => HostResolution.InvokeAsync(context, next, registry));
    }

    /// <summary>
    /// The third endpoint, after <c>MapPlatformBff</c> and <c>MapPlatformProxy</c>: everything that
    /// is not <c>/bff/*</c> or <c>/api/*</c>, served from the app's live release in S3 (plan row
    /// 2.4; contracts/s3-layout.md). The key is built from registry values and a validated path,
    /// <c>If-None-Match</c> is passed through, the content type comes from the extension, the cache
    /// policy from the path, an extensionless path that accepts HTML gets <c>index.html</c>, and
    /// the platform's reserved paths are never read.
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformAssets(this IEndpointRouteBuilder endpoints)
    {
        // The object store is resolved per request, not here: in the gateway it is AddPlatformRegistry's
        // S3 store (task 2.2), whose client throws on construction with no region or bucket configured,
        // and RegistryPoller defers it for that reason so the gateway starts with no Registry:* at all
        // (CI's G1a smoke test). Resolving it at map time would undo that.
        Assets assets = new(endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Hosting.Assets"));

        // A catch-all with every method, ordered last. Not the parameterless MapFallback: its pattern is
        // {*path:nonfile}, which never matches a path with an extension, so /app.js would miss
        // (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.endpointroutebuilderextensions.mapfallback).
        // Every method, so a POST to an asset path gets the contract's 405 problem rather than routing's
        // empty one. Order int.MaxValue, as MapFallback sets it, so /bff and /api endpoints always win.
        endpoints.MapFallback("/{**path}", assets.InvokeAsync);

        return endpoints;
    }
}
