using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Spike;

/// <summary>
/// The provider-specific part, as ADR-0011's IIdpProfile puts it, reduced to what G0 needs. Cognito's logout is
/// GET {BrowserOrigin}/logout?client_id=..&amp;logout_uri=.. and lands on "/" (R-C7). When discovery lists no revocation
/// endpoint, the profile supplies one (Q16): {BrowserOrigin}/oauth2/revoke on Cognito.
/// </summary>
public sealed record IdpProfile(string Name, string BrowserOrigin, string RevokePath)
{
    public string LogoutUrl(string clientId, string host)
        => $"{BrowserOrigin}/logout?client_id={Uri.EscapeDataString(clientId)}&logout_uri={Uri.EscapeDataString($"https://{host}/")}";

    public string RevocationEndpoint => BrowserOrigin + RevokePath;
}

/// <summary>What the gateway saw, for the checks to read. Nothing here is printed as it stands: tokens go to the redactor.</summary>
public sealed class GatewayLog
{
    public sealed record Logout(string AppId, string RevokeRoute, string RevokeOutcome, bool SessionRemoved, string? RevokedToken, string? RevokeAcceptedStyle = null);
    public sealed record CodeExchange(string AppId, string Style, string Outcome);

    public readonly ConcurrentQueue<Logout> Logouts = new();
    public readonly ConcurrentQueue<CodeExchange> CodeExchanges = new();
    /// <summary>The redirect_uri each authorize request carried, per app.</summary>
    public readonly ConcurrentDictionary<string, string> RedirectUris = new();
    /// <summary>Whether the authorize request named a resource, per app.</summary>
    public readonly ConcurrentDictionary<string, bool> SentResource = new();
    /// <summary>The raw Cookie header of the last signed-in request on "/" per host. For --manual only. Never printed.</summary>
    public readonly ConcurrentDictionary<string, string> CookieTap = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Q17: when "basic", the next code exchange sends the client secret in a Basic header instead of the form body.</summary>
    public volatile string CodeExchangeStyle = "post";
}

/// <summary>
/// The gateway of the token-and-proxy harness with what Cognito needs and nothing else. Task 3.8's configuration is
/// unchanged: a sentinel scheme, both scheme names on every call, PostBody client authentication. What it adds:
/// a TLS listener for the browser (Cognito sends the browser back to https://{host}:8443), a plain loopback listener for
/// the harness's own calls (it stands where the load balancer stands), the profile's logout and revocation, and a
/// logout in task 3.9's order. YARP is not mapped: G0 item 5 is cited, not run again (Q8).
/// </summary>
public sealed class CognitoGateway
{
    public required WebApplication App { get; init; }
    public required string BackDoor { get; init; }
    public IServiceProvider Services => App.Services;
    public SessionStore Sessions => Services.GetRequiredService<SessionStore>();
    public GatewayLog Log => Services.GetRequiredService<GatewayLog>();

    public static async Task<CognitoGateway> StartAsync(Registry registry, string authority, IdpProfile profile, X509Certificate2 certificate,
        int tlsPort, Redactor redactor)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Configuration["ForwardedHeaders_Enabled"] = "false";
        b.Logging.ClearProviders();                           // the gateway writes no log lines, so none can hold a token
        b.WebHost.UseKestrelHttpsConfiguration();
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, tlsPort, l => l.UseHttps(certificate));   // the browser
            k.Listen(IPAddress.Loopback, 0);                                        // the harness, as the load balancer
        });

        var sw = new TokenSwitches { Sentinel = true, ExplicitSchemes = true, PostBodyClientAuth = true };   // task 3.8, T-B5
        var s = b.Services;
        s.AddHttpContextAccessor();
        s.AddSingleton(registry);
        s.AddSingleton(new Switches());                      // the hardened harness's overrides, all on
        s.AddSingleton(sw);
        s.AddSingleton(profile);
        s.AddSingleton(redactor);
        s.AddSingleton<GatewayLog>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton(new IdpEndpoint(authority));
        s.AddSingleton<SharedOidc>();
        s.AddSingleton<TokenFlow>();

        s.AddAuthentication();
        s.AddTransient<CookieAuthenticationHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        s.AddTransient<OpenIdConnectHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
        s.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptionsSetup>();
        s.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ProfileEvents>();   // after OidcOptionsSetup, before the framework's post-configure
        s.AddSingleton<OptionsInvalidator>();
        s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());

        s.AddOpenIdConnectAccessTokenManagement(o =>
        {
            o.ChallengeScheme = Scheme.Parse(ProxyGateway.SentinelScheme);
            o.ClientCredentialStyle = ClientCredentialStyle.PostBody;
        });

        var app = b.Build();
        app.Services.GetRequiredService<OptionsInvalidator>();
        var log = app.Services.GetRequiredService<GatewayLog>();

        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

        app.Use(async (ctx, next) =>
        {
            if (RequestApp.Resolve(ctx, registry) is null) { await ProxyGateway.Problem(ctx, 404, "not_found"); return; }
            // The callback carries the authorization code and state. Both become canaries before anything can print them.
            if (ctx.Request.Path == "/signin-oidc")
            {
                redactor.Add(ctx.Request.Query["code"]);
                redactor.Add(ctx.Request.Query["state"]);
            }
            try { await next(ctx); }
            catch (AuthenticationFailureException ex)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("login failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });

        app.UseAuthentication();

        app.MapGet("/", async ctx =>
        {
            var auth = await ctx.AuthenticateAsync();
            if (!auth.Succeeded) { await ctx.ChallengeAsync(); return; }
            var entry = RequestApp.Resolve(ctx, registry)!;
            if (ctx.Request.Headers.Cookie.ToString() is { Length: > 0 } cookie)
            {
                redactor.Add(cookie);
                log.CookieTap[entry.Host] = cookie;
            }
            await ctx.Response.WriteAsync($"shell app={entry.AppId}");
        });

        app.MapGet("/bff/login", async ctx =>
        {
            await ctx.SignOutAsync();
            await ctx.ChallengeAsync(new AuthenticationProperties { RedirectUri = "/" });
        });

        // Task 3.9's order with the profile's route (Q12, Q16): revoke the refresh token read from the session record now,
        // through the library, or through the profile's endpoint when discovery lists none; remove the session whether
        // or not that worked; then send the browser to the provider's logout.
        app.MapPost("/bff/logout", async ctx =>
        {
            var entry = RequestApp.Resolve(ctx, registry)!;
            var flow = ctx.RequestServices.GetRequiredService<TokenFlow>();
            if (ctx.User.Identity?.IsAuthenticated == true && flow.SessionKey(ctx, entry) is { } key && ctx.RequestServices.GetRequiredService<SessionStore>() is var store
                && store.TryGet(key) is { } record && record.AppId == entry.AppId)
            {
                var refreshToken = TicketSerializer.Default.Deserialize(record.Ticket)!.Properties.GetTokenValue("refresh_token");
                string route = "none", outcome = "no refresh token";
                string? acceptedStyle = null;
                if (refreshToken is { Length: > 0 })
                {
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await ctx.RequestServices.GetRequiredService<IOpenIdConnectUserTokenEndpoint>().RevokeRefreshTokenAsync(
                            new UserRefreshToken(RefreshToken.Parse(refreshToken), null),
                            new UserTokenRequestParameters { ChallengeScheme = Scheme.Parse(SchemeNames.Oidc(entry.AppId)) }, deadline.Token);
                        route = "library"; outcome = "sent in the form body; the library does not return the answer";
                    }
                    catch (InvalidOperationException)
                    {
                        // T-B14: no revocation endpoint in discovery, so the library throws. The profile supplies one.
                        route = "profile";
                        var (_, _, sent, style) = await Revocation.SendAsync(ctx.RequestServices.GetRequiredService<SharedOidc>().Backchannel,
                            profile.RevocationEndpoint, entry.ClientId, entry.ClientSecret, refreshToken);
                        outcome = sent;
                        acceptedStyle = style;
                    }
                    catch (OperationCanceledException) { route = "library"; outcome = "no answer in 10 s"; }
                }
                var removed = store.Remove(key);
                log.Logouts.Enqueue(new GatewayLog.Logout(entry.AppId, route, outcome, removed, refreshToken, acceptedStyle));
            }
            await ctx.SignOutAsync(SchemeNames.Cookie(entry.AppId));
            await ctx.SignOutAsync(SchemeNames.Oidc(entry.AppId), new AuthenticationProperties { RedirectUri = "/" });
        });

        app.MapGet("/bff/session", (HttpContext ctx) =>
            ctx.User.Identity?.IsAuthenticated == true
                ? Results.Json(new { sub = ctx.User.FindFirst("sub")?.Value })
                : Results.Json(new { status = 401, code = "unauthenticated" }, statusCode: 401, contentType: "application/problem+json"));

        // Diagnostic, as in token-and-proxy: the token call on its own. ?force=1 refreshes whether or not the token is due.
        app.MapGet("/bff/token", async (HttpContext ctx, TokenFlow flow) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Json(new { code = "unauthenticated" }, statusCode: 401);
            try
            {
                var (token, code, detail) = await flow.AcquireAsync(ctx, RequestApp.Resolve(ctx, registry)!, force: ctx.Request.Query["force"] == "1", endOnRefusal: false);
                redactor.Add(token);
                return token is null
                    ? Results.Json(new { code, error = detail }, statusCode: TokenFlow.StatusFor(code))
                    : Results.Json(new { code, accessToken = token });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { code = "exception", error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        await app.StartAsync();
        var backDoor = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal) && !a.EndsWith(":" + tlsPort, StringComparison.Ordinal));
        return new CognitoGateway { App = app, BackDoor = backDoor };
    }
}

/// <summary>
/// The profile's events on every app's OIDC options: Cognito's logout (ADR-0011), the redirect_uri log for item 3, and
/// Q17's Basic-header code exchange when the harness asks for one.
/// </summary>
public sealed class ProfileEvents(Registry registry, IdpProfile profile, GatewayLog log, Redactor redactor, SharedOidc shared) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) { }

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (!SchemeNames.TryParseOidc(name, out var appId) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;

        o.Events.OnRedirectToIdentityProvider = ctx =>
        {
            log.RedirectUris[app.AppId] = ctx.ProtocolMessage.RedirectUri;
            log.SentResource[app.AppId] = !string.IsNullOrEmpty(ctx.ProtocolMessage.Resource);
            return Task.CompletedTask;
        };

        // Cognito's discovery lists no end_session_endpoint, and Cognito returns no state, so the logout lands on "/" (R-C7).
        o.Events.OnRedirectToIdentityProviderForSignOut = ctx =>
        {
            ctx.Response.Redirect(profile.LogoutUrl(app.ClientId, app.Host));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };

        o.Events.OnAuthorizationCodeReceived = async ctx =>
        {
            if (log.CodeExchangeStyle != "basic") { log.CodeExchanges.Enqueue(new GatewayLog.CodeExchange(app.AppId, "post", "handler")); return; }
            var request = ctx.TokenEndpointRequest!;
            var configuration = await shared.Metadata.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
            var response = await shared.Backchannel.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
            {
                Address = configuration.TokenEndpoint,
                ClientId = app.ClientId,
                ClientSecret = app.ClientSecret,
                ClientCredentialStyle = ClientCredentialStyle.AuthorizationHeader,
                Code = request.Code,
                RedirectUri = request.RedirectUri,
                CodeVerifier = request.Parameters.TryGetValue("code_verifier", out var v) ? v : null,
            });
            log.CodeExchanges.Enqueue(new GatewayLog.CodeExchange(app.AppId, "basic", response.IsError ? $"error {(int)response.HttpStatusCode} {response.Error}" : "ok"));
            if (response.IsError) { ctx.Fail("the Basic-header code exchange was refused: " + response.Error); return; }
            redactor.Add(response.AccessToken); redactor.Add(response.RefreshToken); redactor.Add(response.IdentityToken);
            ctx.HandleCodeRedemption(new OpenIdConnectMessage(response.Json!.Value.GetRawText()));
        };
    }
}
