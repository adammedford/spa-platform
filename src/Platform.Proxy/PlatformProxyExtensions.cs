using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;
using Yarp.ReverseProxy.Configuration;

namespace Platform.Proxy;

/// <summary>
/// Workstream 4's proxy entry points: the YARP registration, and <c>/api/{name}/{rest}</c> at
/// <c>MapPlatformProxy</c>, among the endpoints that end <c>contracts/pipeline.md</c>.
///
/// The configuration provider and the route are task 4.1's. The token step ahead of the forwarder
/// and the header rules in each direction are task 4.2's (<see cref="AccessTokenStep"/>,
/// <see cref="UpstreamHeaders"/>). Keep these signatures and add no others.
/// </summary>
public static class PlatformProxyExtensions
{
    /// <summary>
    /// YARP, configured from the live registry snapshot rather than from a file. ADR-0006: no
    /// request input reaches destination selection.
    /// </summary>
    public static IHostApplicationBuilder AddPlatformProxy(this IHostApplicationBuilder builder)
    {
        // Every generated route gets the same header rules (task 4.2); a route's own transforms are only
        // its prefix removal (RegistryProxyConfig).
        builder.Services.AddReverseProxy().AddTransforms(UpstreamHeaders.Apply);

        // GetService, not GetRequiredService: AddPlatformRegistry is a stub until task 2.2, and
        // G1a's exit test starts the composed gateway on the stubs. No registry means no route.
        builder.Services.AddSingleton<IProxyConfigProvider>(
            services => new RegistryProxyConfigProvider(services.GetService<IRegistry>()));

        return builder;
    }

    /// <summary>
    /// <c>/api/{name}/{rest}</c> through YARP, and the catch-all behind it. An API name the app does
    /// not list matches no generated route, so it lands on the catch-all and is refused there with
    /// 404 <c>unknown_api</c> rather than falling through to an asset (findings T-C9, T-C10).
    /// </summary>
    public static IEndpointRouteBuilder MapPlatformProxy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReverseProxy(proxy =>
        {
            // The token first, so a 401 or 503 is answered before anything is forwarded and the
            // failure check below never sees it.
            AccessTokenStep.Use(proxy);
            UpstreamFailure.Use(proxy);
        });
        endpoints.Map("/api/{**rest}", UnknownApi.WriteAsync);

        return endpoints;
    }
}
