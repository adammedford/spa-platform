using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Spike;

public enum Mode
{
    /// <summary>The design under test: schemes derived from (request host, registry snapshot).</summary>
    HostScoped,
    /// <summary>Control: the framework's stock provider with one pair of schemes per app added at runtime.</summary>
    StockProviderAllSchemes,
}

public sealed class Gateway
{
    public required WebApplication App { get; init; }
    public required string BaseUrl { get; init; }
    public IServiceProvider Services => App.Services;
    public SessionStore Sessions => Services.GetRequiredService<SessionStore>();

    public static async Task<Gateway> StartAsync(Registry registry, string idpBaseUrl, Mode mode)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");

        var s = b.Services;
        s.AddHttpContextAccessor();
        s.AddSingleton(registry);
        s.AddSingleton<SessionStore>();
        s.AddSingleton(new IdpEndpoint(idpBaseUrl));
        s.AddSingleton<SharedBackchannel>();
        s.AddSingleton<SessionTokens>();

        s.AddAuthentication();   // authentication core, data protection, encoders, TimeProvider

        // Handler services are registered once and belong to no scheme in particular.
        s.AddTransient<CookieAuthenticationHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        s.AddTransient<StandInOidcHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<StandInOidcOptions>, StandInPostConfigure>());

        // Per-app options are built from the registry, by scheme name, the first time the framework asks.
        s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
        s.AddSingleton<IConfigureOptions<StandInOidcOptions>, OidcOptionsSetup>();
        s.AddSingleton<OptionsInvalidator>();

        if (mode == Mode.HostScoped)
            s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());

        var app = b.Build();
        app.Services.GetRequiredService<OptionsInvalidator>();   // subscribe before the first publish

        if (mode == Mode.StockProviderAllSchemes)
        {
            // The documented runtime API on the stock provider. Works, but every scheme is global.
            var provider = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            foreach (var a in registry.Current.ByAppId.Values.OrderBy(x => x.AppId, StringComparer.Ordinal))
            {
                provider.AddScheme(new AuthenticationScheme(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler)));
                provider.AddScheme(new AuthenticationScheme(SchemeNames.Oidc(a.AppId), null, typeof(StandInOidcHandler)));
            }
        }

        // Stand-in for "TLS terminates at the ALB": trust X-Forwarded-Proto from the loopback proxy only.
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/healthz") { ctx.Response.StatusCode = 200; return; }
            if (RequestApp.Resolve(ctx, registry) is null) { ctx.Response.StatusCode = 404; return; }   // unknown host: nothing else runs
            try { await next(ctx); }
            catch (AuthenticationFailureException ex)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("login failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });

        app.UseAuthentication();

        // In host-scoped mode every call below uses the DEFAULT scheme (null): the provider picks this host's scheme.
        string? CookieScheme(HttpContext c) => mode == Mode.HostScoped ? null : SchemeNames.Cookie(RequestApp.Resolve(c, registry)!.AppId);
        string? OidcScheme(HttpContext c) => mode == Mode.HostScoped ? null : SchemeNames.Oidc(RequestApp.Resolve(c, registry)!.AppId);

        app.MapGet("/", async ctx =>
        {
            var auth = await ctx.AuthenticateAsync(CookieScheme(ctx));
            if (!auth.Succeeded) { await ctx.ChallengeAsync(OidcScheme(ctx)); return; }
            await ctx.Response.WriteAsync($"shell app={RequestApp.Resolve(ctx, registry)!.AppId} sub={auth.Principal!.FindFirst("sub")?.Value}");
        });

        app.MapGet("/bff/user", async ctx =>
        {
            var auth = await ctx.AuthenticateAsync(CookieScheme(ctx));
            if (!auth.Succeeded) { ctx.Response.StatusCode = 401; return; }
            await ctx.Response.WriteAsJsonAsync(new
            {
                app = RequestApp.Resolve(ctx, registry)!.AppId,
                sub = auth.Principal!.FindFirst("sub")?.Value,
                clientId = auth.Principal.FindFirst("client_id")?.Value,
                middlewareSetUser = ctx.User.Identity?.IsAuthenticated == true,
            });
        });

        // Stands in for a proxied API call: what would the upstream see in the Authorization header?
        app.MapGet("/api/whoami", async (HttpContext ctx, SessionTokens tokens) =>
        {
            var token = await tokens.GetAccessTokenAsync(ctx);
            if (token is null) return Results.StatusCode(401);
            var parts = token.Split('~');
            return Results.Json(new { tokenClient = parts[1], tokenSeq = int.Parse(parts[3]) });
        });

        app.MapPost("/bff/logout", async ctx =>
        {
            await ctx.SignOutAsync(CookieScheme(ctx));
            ctx.Response.StatusCode = 204;
        });

        app.MapGet("/_probe", async (HttpContext ctx, IAuthenticationSchemeProvider schemes, string scheme) =>
            (await schemes.GetSchemeAsync(scheme)) is null ? "absent" : "present");

        await app.StartAsync();
        return new Gateway
        {
            App = app,
            BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(),
        };
    }
}
