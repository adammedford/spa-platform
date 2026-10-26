using Amazon.DynamoDBv2;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// Workstream 3's entry points in <c>contracts/pipeline.md</c>: <c>AddPlatformIdentity</c> in the
/// service table, <see cref="UsePlatformShellGate"/>, and <see cref="MapPlatformBff"/> for
/// <c>/bff/*</c>. These signatures are fixed by the pipeline contract.
/// </summary>
public static class PlatformIdentityExtensions
{
    /// <summary>
    /// The scheme provider, the OIDC and cookie options, the session store, the token source behind
    /// <see cref="IAccessTokenSource"/>, logout's session end and the profiles. Tasks 3.1 to 3.9.
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

        // The session cookie of task 3.3, configured ahead of that post-configuration as AddCookie's
        // own configure delegate would be.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>());

        // The OpenID Connect options of task 3.2. The framework's post-configuration is registered as
        // AddOpenIdConnect would register it, for the same reason as the cookie handler's above; it
        // supplies the state and nonce protectors and keeps the shared backchannel and metadata manager
        // this setup sets. The validation AddOpenIdConnect would add for one fixed name is registered
        // for every OpenID Connect scheme instead (R-D9). AddOpenIdConnect's own registrations:
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectExtensions.cs
        services.TryAddSingleton<IssuerDirectory>();

        // The options invalidator of task 3.6. Both options setups depend on it, so it subscribes to
        // the registry before the first scheme's options are built.
        services.TryAddSingleton<OptionsInvalidator>();

        // The caches the invalidator evicts from, in place of the framework's: a closed registration wins
        // over the open-generic OptionsCache<> that AddOptions registers. They keep no failed build
        // (R-D10) and no options built for an entry a swap has replaced.
        services.TryAddSingleton<IOptionsMonitorCache<CookieAuthenticationOptions>>(sp =>
            new AppOptionsCache<CookieAuthenticationOptions>(
                sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<IRegistry>(), CookieOptionsSetup.Prefix));
        services.TryAddSingleton<IOptionsMonitorCache<OpenIdConnectOptions>>(sp =>
            new AppOptionsCache<OpenIdConnectOptions>(
                sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<IRegistry>(), OidcOptionsSetup.Prefix));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<OpenIdConnectOptions>, OidcOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<OpenIdConnectOptions>, OidcOptionsValidation>());

        // The profiles of task 3.7, one per value of Issuers:{name}:Profile. The options setup picks the
        // first registered under the issuer's name, so a host that registers its own ahead of these wins.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdpProfile, StandardIdpProfile>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIdpProfile, CognitoIdpProfile>());

        // The session store of task 3.4 or of P2-1, as Session:Store names it, unless the host brought its
        // own, and over the same table the refresh lease of P2-1 Decision 7. Built on first use, not at
        // start-up: a gateway that never touches a session (the ALB health check, tools/smoke.sh) needs
        // no session settings. The DynamoDB client comes from the container, so a host that registers its
        // own first wins, as AddPlatformRegistry registers IAmazonS3 (change request P2-1, Decision 3); the
        // default reads its Region and credentials from the environment, the task role in a cell. The lease
        // is null under the memory store or none named, which the container hands to an optional
        // dependency as it would an unregistered service, and GetRequiredService refuses (#393).
        services.TryAddSingleton<ISessionStore>(SessionStores.FromConfiguration);
        services.TryAddSingleton<IRefreshLeases>(provider => SessionStores.LeasesFromConfiguration(provider)!);
        services.TryAddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
        services.TryAddSingleton(DynamoDbSessionTable.FromConfiguration);

        // The token source of task 3.8, over the token library (ADR-0005). The library's options hook gets
        // the sentinel as its default challenge scheme, so it binds to no app (T-B1, T-B2); the source
        // names both schemes on every call and calls the library's endpoint in the credential style of
        // the app's issuer (ADR-0017), one refresh at a time per session.
        services.AddOpenIdConnectAccessTokenManagement(o => o.ChallengeScheme = Scheme.Parse(ProfileTokenEndpoints.SentinelScheme));
        Type endpointType = ProfileTokenEndpoints.RegisteredEndpointType(services);
        services.TryAddSingleton(sp => new ProfileTokenEndpoints(sp, endpointType));
        services.TryAddSingleton<AppSessions>();
        services.TryAddSingleton<AccessTokenSourceOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CredentialStyles>();
        services.TryAddSingleton<AccessTokenSource>();

        // The contract's interface, forwarded to that one instance. The proxy's token step asks it for
        // every call the proxy would forward, and only a session yields a token, so such a call without
        // one gets 401 (contracts/http-contract.md -> Errors).
        services.TryAddSingleton<IAccessTokenSource>(sp => sp.GetRequiredService<AccessTokenSource>());

        // Logout's revocation and removal of task 3.9, under the same gates and in the same style.
        services.TryAddSingleton<SessionEnd>();
        return builder;
    }

    /// <summary>
    /// A session before any asset (ADR-0008): an unauthenticated navigation is challenged, an
    /// unauthenticated fetch gets 401. See <see cref="ShellGate"/>. Installed whatever the host
    /// registered: a gate that a missing registration could switch off would fail open, and serve every
    /// asset without a session in exactly the host that missed it.
    /// </summary>
    public static IApplicationBuilder UsePlatformShellGate(this IApplicationBuilder app) => app.Use(ShellGate.InvokeAsync);

    /// <summary><c>/bff/session</c>, <c>/bff/login</c> and <c>/bff/logout</c>. Task 3.9; see <see cref="Bff"/>.</summary>
    public static IEndpointRouteBuilder MapPlatformBff(this IEndpointRouteBuilder endpoints)
    {
        Bff.Map(endpoints);
        return endpoints;
    }
}
