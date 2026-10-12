using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Spike;

public enum Mode
{
    /// <summary>The plan's design: schemes derived from (request host, registry snapshot).</summary>
    HostScoped,
    /// <summary>Control: the framework's stock provider with one pair of schemes per app added at runtime.</summary>
    StockProviderAllSchemes,
}

public sealed class Gateway
{
    public static long OidcHandlersCreated;          // real OpenIdConnectHandler instances resolved from DI

    public required WebApplication App { get; init; }
    public required string BaseUrl { get; init; }
    public IServiceProvider Services => App.Services;
    public SessionStore Sessions => Services.GetRequiredService<SessionStore>();

    public static async Task<Gateway> StartAsync(Registry registry, string idpBaseUrl, Switches sw)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");

        var s = b.Services;
        s.AddHttpContextAccessor();
        s.AddSingleton(registry);
        s.AddSingleton(sw);
        s.AddSingleton<SessionStore>();
        s.AddSingleton(new IdpEndpoint(idpBaseUrl));
        s.AddSingleton<SharedOidc>();

        s.AddAuthentication();

        // Handler services, registered once, bound to no scheme. Nothing here names an app.
        s.AddTransient<CookieAuthenticationHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        s.AddTransient(sp => { Interlocked.Increment(ref OidcHandlersCreated); return ActivatorUtilities.CreateInstance<OpenIdConnectHandler>(sp); });
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());

        s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
        s.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptionsSetup>();
        s.AddSingleton<OptionsInvalidator>();

        s.AddAntiforgery(o =>
        {
            o.Cookie.Name = "__Host-spa-csrf";
            o.HeaderName = "X-CSRF-TOKEN";
            if (sw.AntiforgerySecureAlways) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.SuppressReadingTokenFromFormBody = sw.AntiforgerySuppressFormBody;
        });
        if (sw.AntiforgeryBindApp) s.AddSingleton<IAntiforgeryAdditionalDataProvider, AppBoundAntiforgeryData>();

        if (sw.Mode == Mode.HostScoped)
            s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());

        var app = b.Build();
        app.Services.GetRequiredService<OptionsInvalidator>();

        if (sw.Mode == Mode.StockProviderAllSchemes)
        {
            var provider = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            foreach (var a in registry.Current.ByAppId.Values.OrderBy(x => x.AppId, StringComparer.Ordinal))
            {
                provider.AddScheme(new AuthenticationScheme(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler)));
                provider.AddScheme(new AuthenticationScheme(SchemeNames.Oidc(a.AppId), null, typeof(OpenIdConnectHandler)));
            }
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });

        app.Use(async (ctx, next) =>
        {
            if (RequestApp.Resolve(ctx, registry) is null) { ctx.Response.StatusCode = 404; return; }
            try { await next(ctx); }
            catch (AuthenticationFailureException ex)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("login failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });

        app.UseAuthentication();

        string? CookieScheme(HttpContext c) => sw.Mode == Mode.HostScoped ? null : SchemeNames.Cookie(RequestApp.Resolve(c, registry)!.AppId);
        string? OidcScheme(HttpContext c) => sw.Mode == Mode.HostScoped ? null : SchemeNames.Oidc(RequestApp.Resolve(c, registry)!.AppId);

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
                accessToken = auth.Properties!.GetTokenValue("access_token"),
                middlewareSetUser = ctx.User.Identity?.IsAuthenticated == true,
            });
        });

        // The plan's /bff/login: "Discards any existing session, then redirects to the provider."
        app.MapGet("/bff/login", async ctx =>
        {
            await ctx.SignOutAsync(CookieScheme(ctx));
            await ctx.ChallengeAsync(OidcScheme(ctx), new AuthenticationProperties { RedirectUri = "/" });
        });

        // Local sign-out, then RP-initiated logout through the real handler.
        app.MapPost("/bff/logout", async ctx =>
        {
            await ctx.SignOutAsync(CookieScheme(ctx));
            await ctx.SignOutAsync(OidcScheme(ctx) ?? (await ctx.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultChallengeSchemeAsync())!.Name,
                new AuthenticationProperties { RedirectUri = "/" });
        });

        app.MapGet("/bff/session", (HttpContext ctx, IAntiforgery af) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.StatusCode(401);
            var t = af.GetAndStoreTokens(ctx);
            return Results.Json(new { sub = ctx.User.FindFirst("sub")?.Value, csrfToken = t.RequestToken });
        });

        // Stands in for a proxied unsafe API call. Reports whether the token check passed and how much of the
        // request body was still unread afterwards (what YARP would have left to forward).
        app.MapPost("/api/echo", async (HttpContext ctx, IAntiforgery af) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.StatusCode(401);
            var valid = await af.IsRequestValidAsync(ctx);
            using var reader = new StreamReader(ctx.Request.Body);
            var left = (await reader.ReadToEndAsync()).Length;
            return Results.Json(new { valid, bodyLeft = left }, statusCode: valid ? 200 : 403);
        });

        app.MapGet("/_probe", async (IAuthenticationSchemeProvider schemes, string scheme) =>
            (await schemes.GetSchemeAsync(scheme)) is null ? "absent" : "present");

        await app.StartAsync();
        return new Gateway
        {
            App = app,
            BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(),
        };
    }
}
