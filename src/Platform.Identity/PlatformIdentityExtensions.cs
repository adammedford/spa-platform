using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Platform.Identity;

/// <summary>
/// Workstream 3's entry points: stage 8 of <c>contracts/pipeline.md</c>, and <c>/bff/*</c> at
/// stage 10.
///
/// <b>Stubs, and they stay stubs the longest.</b> Task I-4 wrote them so <c>Program.cs</c> can call
/// the frozen order; nothing else in this project starts before task I-6 tags <c>contracts-v1</c>,
/// because Gate G0 has still to report on Cognito. Tasks 3.1 to 3.9 fill them in. Keep these
/// signatures and add no others.
/// </summary>
public static class PlatformIdentityExtensions
{
    /// <summary>
    /// The scheme provider, the OIDC and cookie options, the session store, the token source and the
    /// profiles. Tasks 3.1 to 3.8.
    ///
    /// The stub registers the framework's authentication services and nothing else. That is not a
    /// convenience: stage 7 of the pipeline is the framework's own <c>UseAuthentication</c>, and its
    /// middleware cannot be built without <c>IAuthenticationSchemeProvider</c>, so the frozen order
    /// would not start at all. Authentication services belong to this entry point rather than to
    /// <c>Program.cs</c>, which calls the six entry points in the service table and nothing else.
    /// With no schemes registered the middleware authenticates nobody, which is correct here.
    /// Task 3.1 replaces the scheme provider with the host-resolved one (ADR-0001).
    /// </summary>
    public static IHostApplicationBuilder AddPlatformIdentity(this IHostApplicationBuilder builder)
    {
        builder.Services.AddAuthentication();
        return builder;
    }

    /// <summary>
    /// A session before any asset (ADR-0008): an unauthenticated navigation is challenged, an
    /// unauthenticated fetch gets 401. Task 3.9.
    /// </summary>
    public static IApplicationBuilder UsePlatformShellGate(this IApplicationBuilder app) => app;

    /// <summary><c>/bff/session</c>, <c>/bff/login</c> and <c>/bff/logout</c>. Task 3.9.</summary>
    public static IEndpointRouteBuilder MapPlatformBff(this IEndpointRouteBuilder endpoints) => endpoints;
}
