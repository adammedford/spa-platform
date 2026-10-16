using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Platform.Identity;

/// <summary>
/// Workstream 3's entry points in <c>contracts/pipeline.md</c>: <c>AddPlatformIdentity</c> in the
/// service table, <see cref="UsePlatformShellGate"/>, and <see cref="MapPlatformBff"/> for
/// <c>/bff/*</c>. Tasks 3.1 to 3.9 fill them in. Keep these signatures and add no others.
/// </summary>
public static class PlatformIdentityExtensions
{
    /// <summary>
    /// The scheme provider, the OIDC and cookie options, the session store, the token source and the
    /// profiles. Tasks 3.1 to 3.8.
    ///
    /// Authentication services belong to this entry point rather than to <c>Program.cs</c>, which
    /// calls the six entry points in the service table and nothing else: the framework's own
    /// <c>UseAuthentication</c>, which runs after <c>UsePlatformRequestGate</c>, cannot be built
    /// without an <see cref="IAuthenticationSchemeProvider"/>. Task 3.1 replaces the framework's
    /// provider with <see cref="RegistrySchemeProvider"/> (ADR-0001).
    /// </summary>
    public static IHostApplicationBuilder AddPlatformIdentity(this IHostApplicationBuilder builder)
    {
        IServiceCollection services = builder.Services;
        services.AddAuthentication();

        // The scheme provider reads the app host resolution pinned on the current request.
        services.AddHttpContextAccessor();
        services.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());

        // The framework's own post-configuration for the cookie handler, bound to no scheme name, as
        // AddCookie would register it (ADR-0001: "the framework's own post-configuration then runs as
        // usual"). Without it a cookie scheme's options have no CookieManager or TicketDataFormat, and
        // the authentication middleware's default authenticate on every pinned request throws.
        // AddCookie itself is not called because it would also name a scheme every host shares. AddCookie's
        // own registration of this post-configuration:
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieExtensions.cs
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
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
