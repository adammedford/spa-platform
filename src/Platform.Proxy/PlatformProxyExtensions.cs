using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Platform.Proxy;

/// <summary>
/// Workstream 4's proxy entry points: the YARP registration, and <c>/api/{name}/{rest}</c> at
/// stage 10 of <c>contracts/pipeline.md</c>.
///
/// <b>Stubs.</b> Task I-4 wrote these so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Task 4.1 fills in the configuration provider, and task 4.2 the pipeline that acquires
/// the token ahead of the forwarder and strips the headers the upstream contract forbids. Task 4.2
/// waits for G1b. Keep these signatures and add no others.
/// </summary>
public static class PlatformProxyExtensions
{
    public static IHostApplicationBuilder AddPlatformProxy(this IHostApplicationBuilder builder) => builder;

    public static IEndpointRouteBuilder MapPlatformProxy(this IEndpointRouteBuilder endpoints) => endpoints;
}
