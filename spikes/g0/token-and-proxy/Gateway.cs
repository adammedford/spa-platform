using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Spike;

// Auth.cs, shared with the real-handler harness, names this enum. It is declared in that harness's
// Gateway.cs, which this project does not compile, so it is repeated here.
public enum Mode
{
    HostScoped,
    StockProviderAllSchemes,
}

public enum TokenStep
{
    /// <summary>The plan's default: a step in the proxy pipeline gets the token and answers 401 itself.</summary>
    Pipeline,
    /// <summary>The alternative the plan names: a YARP request transform gets the token and short-circuits.</summary>
    Transform,
}

/// <summary>Knobs for the checks. The defaults are the configuration the plan gives task 3.8 and task 4.2.</summary>
public sealed class TokenSwitches
{
    public bool Sentinel = true;            // UserTokenManagementOptions.ChallengeScheme = a name no app can have
    public bool ExplicitSchemes = true;     // ChallengeScheme and SignInScheme on every call
    public bool PostBodyClientAuth;         // ClientCredentialStyle.PostBody instead of the library's default
    public bool RecheckOnRefusal;           // before ending a session on a refused refresh, look in the store for a newer refresh token
    public bool PassResource;               // put the entry's resource on every token call
    public bool PassRequestAborted;         // hand the request's cancellation token to the library's token call
    public bool StoreHonoursCancellation;   // the ticket store gives up a renew when the handler's cancellation token has fired
    public bool OwnOrchestration;           // section D: the gateway decides when to refresh; the library makes the refresh and revoke calls only
    public int ProviderDeadlineMs = 10_000; // section D: how long a refresh or revoke call may take. It is tied to no request
    public bool StripOnSuccessOnly;         // YARP's default ResponseCondition for removing a response header
    public bool YarpDefaultHeadersOnly;     // leave request forwarding headers to YARP's defaults
    public TokenStep TokenStep = TokenStep.Pipeline;
    public TimeProvider? Clock;             // registered as the TimeProvider service when set
}

/// <summary>One API an app may call. In the platform this is an item of the entry's "apis" list.</summary>
public sealed record ApiDef(string Name, string Upstream, int TimeoutSeconds = 30);

public sealed class ApiCatalog
{
    private readonly ConcurrentDictionary<string, ApiDef[]> _apis = new(StringComparer.Ordinal);
    public void Set(string appId, params ApiDef[] apis) => _apis[appId] = apis;
    public ApiDef[] For(string appId) => _apis.TryGetValue(appId, out var a) ? a : [];
}

/// <summary>Plan task 4.1: one route per app and API, matched on host and /api/{name}/**, prefix removed.</summary>
public static class ProxyConfig
{
    public static string RouteId(string appId, string api) => $"{appId}/{api}";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(RegistrySnapshot snapshot, ApiCatalog catalog)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        foreach (var app in snapshot.ByAppId.Values.OrderBy(a => a.AppId, StringComparer.Ordinal))
        {
            foreach (var api in catalog.For(app.AppId))
            {
                var id = RouteId(app.AppId, api.Name);
                routes.Add(new RouteConfig
                {
                    RouteId = id,
                    ClusterId = id,
                    Match = new RouteMatch { Hosts = [app.Host], Path = $"/api/{api.Name}/{{**rest}}" },
                }.WithTransformPathRemovePrefix($"/api/{api.Name}"));
                clusters.Add(new ClusterConfig
                {
                    ClusterId = id,
                    Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal) { ["only"] = new DestinationConfig { Address = api.Upstream } },
                    // An idle limit: YARP restarts it on response headers and on every body read or write.
                    HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(api.TimeoutSeconds) },
                });
            }
        }
        return (routes, clusters);
    }
}

/// <summary>For probe B17: a ticket store that, unlike the in-memory one, honours the cancellation token the cookie handler passes to a renew.</summary>
public sealed class CancellationHonouringTicketStore(ITicketStore inner) : ITicketStore
{
    public Task<string> StoreAsync(AuthenticationTicket ticket) => inner.StoreAsync(ticket);
    public Task RenewAsync(string key, AuthenticationTicket ticket) => inner.RenewAsync(key, ticket);
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => inner.RetrieveAsync(key);
    public Task RemoveAsync(string key) => inner.RemoveAsync(key);

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.RenewAsync(key, ticket);
    }
}

/// <summary>
/// What plan task 3.8 calls IAccessTokenSource, reduced to what the checks need. Two shapes:
/// the library's token manager with both schemes named (sections A to C), and the gateway's own orchestration
/// around the library's refresh and revoke calls (section D).
/// The error text in a result is for the checks to read. The platform's problem bodies carry the code only.
/// </summary>
public sealed class TokenFlow(TokenSwitches sw, SessionStore sessions, IOptionsMonitor<CookieAuthenticationOptions> cookies)
{
    public const string TokenKey = "g0.access-token";
    public const string RefusedKey = "g0.refused";

    /// <summary>How long a failed refresh is remembered, so the calls already waiting behind it do not each ask the provider again.</summary>
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMilliseconds(250);
    /// <summary>The refresh and revoke calls have their own deadline. They are tied to no request.</summary>
    private TimeSpan ProviderDeadline => TimeSpan.FromMilliseconds(sw.ProviderDeadlineMs);

    private sealed record Failure(long At, string Code, string? Detail);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a request sent with X-Test-Hold has authenticated and is waiting to ask for its token.</summary>
    public Task Parked => _parked.Task;
    public void Release() => _hold.TrySetResult();
    public void ResetHold()
    {
        _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The status the harness gives each outcome. The browser contract has no entry for the last one.</summary>
    public static int StatusFor(string code) => code == "idp_unavailable" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;

    public UserTokenRequestParameters? Parameters(AppEntry app, bool force = false, bool? explicitSchemes = null)
    {
        var named = explicitSchemes ?? sw.ExplicitSchemes;
        var resource = sw.PassResource && app.Resource is not null;
        if (!named && !force && !resource) return null;
        return new UserTokenRequestParameters
        {
            ChallengeScheme = named ? Scheme.Parse(SchemeNames.Oidc(app.AppId)) : null,
            SignInScheme = named ? Scheme.Parse(SchemeNames.Cookie(app.AppId)) : null,
            ForceTokenRenewal = force,
            Resource = resource ? Resource.Parse(app.Resource!) : null,
        };
    }

    /// <summary>Returns the token, or null with a code: unauthenticated, session_ended or idp_unavailable.</summary>
    public async Task<(string? Token, string Code, string? Detail)> AcquireAsync(HttpContext ctx, AppEntry app,
        bool force = false, bool? explicitSchemes = null, bool endOnRefusal = true)
    {
        // Test hook: time passes between authentication and the token call. The session has been read by now.
        if (ctx.Request.Headers.ContainsKey("X-Test-Hold"))
        {
            _parked.TrySetResult();
            await _hold.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        if (sw.OwnOrchestration) return await OwnAsync(ctx, app, force);

        var result = await ctx.GetUserAccessTokenAsync(Parameters(app, force, explicitSchemes), sw.PassRequestAborted ? ctx.RequestAborted : CancellationToken.None);
        if (result.Succeeded) return (result.Token.AccessToken.ToString(), "ok", null);

        // The library reports every failure the same way, with the error as a string. Only the provider's refusal of
        // the refresh token ends a session; an outage or an unreachable provider must not sign everyone out.
        var error = result.FailedResult.Error;
        if (error != "invalid_grant") return (null, "idp_unavailable", error);
        if (sw.RecheckOnRefusal && await NewerAccessTokenAsync(ctx, app) is { } newer) return (newer, "ok-after-recheck", error);

        // Plan task 3.8: "A refused refresh ends the session." The library leaves the session alone.
        if (endOnRefusal) await ctx.SignOutAsync(SchemeNames.Cookie(app.AppId));
        return (null, "session_ended", error);
    }

    /// <summary>
    /// The guard for a refused refresh: if the store now holds a different refresh token from the one this request
    /// read when it authenticated, another request refreshed in between, and its access token is the one to use.
    /// </summary>
    private async Task<string?> NewerAccessTokenAsync(HttpContext ctx, AppEntry app)
    {
        var scheme = SchemeNames.Cookie(app.AppId);
        var seen = (await ctx.AuthenticateAsync(scheme)).Properties?.GetTokenValue("refresh_token");
        if (SessionKey(ctx, app) is not { } key || sessions.TryGet(key) is not { } record || record.AppId != app.AppId) return null;
        var stored = TicketSerializer.Default.Deserialize(record.Ticket)!.Properties;
        var current = stored.GetTokenValue("refresh_token");
        return current is not null && current != seen ? stored.GetTokenValue("access_token") : null;
    }

    private SemaphoreSlim GateFor(string key) => _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));   // one per session and never removed here; the platform would bound this

    private static void Put(AuthenticationProperties properties, string name, string value)
    {
        if (properties.UpdateTokenValue(name, value)) return;
        // The login saved no such token, as when the provider sent no expires_in. Add it the way SaveTokens would have.
        properties.Items[".Token." + name] = value;
        properties.Items[".TokenNames"] = properties.Items.TryGetValue(".TokenNames", out var names) && !string.IsNullOrEmpty(names) ? names + ";" + name : name;
    }

    /// <summary>
    /// Section D. One refresh per session: the decision is taken under the session's gate, after reading the session
    /// from the store, not from the ticket this request authenticated with. The library makes the refresh call and
    /// nothing else; the new tokens go straight into the session record, so no cookie is issued.
    /// </summary>
    private async Task<(string? Token, string Code, string? Detail)> OwnAsync(HttpContext ctx, AppEntry app, bool force)
    {
        if (SessionKey(ctx, app) is not { } key) return (null, "unauthenticated", null);
        var gate = GateFor(key);
        await gate.WaitAsync();
        try
        {
            if (sessions.TryGet(key) is not { } record || record.AppId != app.AppId) return (null, "unauthenticated", null);
            var ticket = TicketSerializer.Default.Deserialize(record.Ticket)!;
            var properties = ticket.Properties;

            // No expires_at means the provider gave no lifetime: like the library, treat the token as not known to expire.
            var expires = DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MaxValue;
            if (!force && properties.GetTokenValue("access_token") is { Length: > 0 } current && expires > DateTimeOffset.UtcNow.AddMinutes(1)) return (current, "ok", null);

            // The calls that queued behind a refresh that has just failed share its failure instead of each asking the provider again.
            if (!force && _failures.TryGetValue(key, out var recent) && Stopwatch.GetElapsedTime(recent.At) < FailureMemory) return (null, recent.Code, recent.Detail);

            if (properties.GetTokenValue("refresh_token") is not { Length: > 0 } refreshToken)
            {
                await ctx.SignOutAsync(SchemeNames.Cookie(app.AppId));
                return (null, "session_ended", "the session holds no refresh token");
            }

            // The protocol step is the library's. Its deadline is its own: a browser that disconnects cannot strand a rotated token.
            string? failure;
            UserToken? token = null;
            try
            {
                using var deadline = new CancellationTokenSource(ProviderDeadline);
                var endpoint = ctx.RequestServices.GetRequiredService<IOpenIdConnectUserTokenEndpoint>();
                var result = await endpoint.RefreshAccessTokenAsync(new UserRefreshToken(RefreshToken.Parse(refreshToken), null),
                    new UserTokenRequestParameters { ChallengeScheme = Scheme.Parse(SchemeNames.Oidc(app.AppId)) }, deadline.Token);
                failure = result.Succeeded ? null : result.FailedResult.Error;
                token = result.Token;
            }
            catch (OperationCanceledException)
            {
                failure = "the provider did not answer in time";
            }

            if (failure == "invalid_grant")
            {
                await ctx.SignOutAsync(SchemeNames.Cookie(app.AppId));       // removes the record and clears the cookie
                return (null, "session_ended", failure);
            }
            if (failure is not null || token is null)
            {
                _failures[key] = new Failure(Stopwatch.GetTimestamp(), "idp_unavailable", failure);
                return (null, "idp_unavailable", failure);
            }

            _failures.TryRemove(key, out _);
            Put(properties, "access_token", token.AccessToken.ToString());
            Put(properties, "expires_at", token.Expiration.ToString("o", CultureInfo.InvariantCulture));
            if (token.RefreshToken is { } rotated) Put(properties, "refresh_token", rotated.ToString());
            if (token.IdentityToken is { } identity) Put(properties, "id_token", identity.ToString());   // a response without one leaves the stored one alone

            // Logout takes the same gate (EndAsync), so a logout cannot slip between the read above and this write.
            // The platform's ISessionStore.ReplaceAsync, with the expected version, covers every other way a record
            // can go, and other processes.
            sessions.Set(key, record with { Ticket = TicketSerializer.Default.Serialize(ticket) });
            return (token.AccessToken.ToString(), "ok", null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Section D's logout. It waits for a refresh in flight, then revokes the refresh token the store holds now, which
    /// may be newer than the one in this request's ticket, and removes the session whether or not the provider could revoke.
    /// </summary>
    public async Task EndAsync(HttpContext ctx, AppEntry app)
    {
        if (SessionKey(ctx, app) is not { } key) return;
        var gate = GateFor(key);
        await gate.WaitAsync();
        try
        {
            if (sessions.TryGet(key) is not { } record || record.AppId != app.AppId) return;
            var refreshToken = TicketSerializer.Default.Deserialize(record.Ticket)!.Properties.GetTokenValue("refresh_token");
            if (refreshToken is { Length: > 0 })
            {
                try
                {
                    using var deadline = new CancellationTokenSource(ProviderDeadline);
                    await ctx.RequestServices.GetRequiredService<IOpenIdConnectUserTokenEndpoint>().RevokeRefreshTokenAsync(
                        new UserRefreshToken(RefreshToken.Parse(refreshToken), null),
                        new UserTokenRequestParameters { ChallengeScheme = Scheme.Parse(SchemeNames.Oidc(app.AppId)) }, deadline.Token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
                {
                    // No revocation endpoint, or no answer. The user is signed out here all the same.
                }
            }
            // Removed here, under the gate, so that a refresh can trust the record it reads. The sign-out that follows
            // would remove it as well, but outside the gate.
            sessions.Remove(key);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The session key inside this request's cookie. The cookie holds one claim, the key, and nothing else.</summary>
    public string? SessionKey(HttpContext ctx, AppEntry app)
    {
        var options = cookies.Get(SchemeNames.Cookie(app.AppId));
        var raw = options.CookieManager.GetRequestCookie(ctx, options.Cookie.Name!);
        return raw is null ? null : options.TicketDataFormat.Unprotect(raw)?.Principal.Claims.FirstOrDefault(c => c.Type.EndsWith("-SessionId", StringComparison.Ordinal))?.Value;
    }
}

/// <summary>
/// The gateway of the real-handler harness with two additions: Duende.AccessTokenManagement for refresh and
/// revocation (G0 item 4), and YARP for /api (G0 item 5). The four overrides of the hardened harness stay on.
/// </summary>
public sealed class ProxyGateway
{
    /// <summary>Not a name the scheme provider can derive: those start "oidc:" or "cookie:".</summary>
    public const string SentinelScheme = "token-management:none";
    public const string GatewayCsp = "default-src 'self'";
    /// <summary>Sent by the one shared backchannel, so the provider's log shows who used it.</summary>
    public const string BackchannelAgent = "g0-shared-backchannel/1.0";

    public required WebApplication App { get; init; }
    public required string BaseUrl { get; init; }
    public required ApiCatalog Apis { get; init; }
    public IServiceProvider Services => App.Services;
    public SessionStore Sessions => Services.GetRequiredService<SessionStore>();
    public TokenFlow Flow => Services.GetRequiredService<TokenFlow>();

    public static Task Problem(HttpContext ctx, int status, string code, string? detail = null)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { status, code, detail }, options: null, contentType: "application/problem+json");
    }

    /// <summary>
    /// The forwarding headers this gateway removes before YARP adds its own. YARP's defaults replace X-Forwarded-For,
    /// -Host, -Proto and -Prefix and drop Forwarded; every other header a browser sends goes upstream as sent.
    /// The list is a choice, and no list of this kind is complete: see C5.
    /// </summary>
    public static bool IsRemovedForwardingHeader(string name)
        => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
           || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase) || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Via", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the host without starting it, so a check can touch the services before any request arrives.</summary>
    public static ProxyGateway Build(Registry registry, string idpBaseUrl, TokenSwitches sw, ApiCatalog catalog)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Configuration["ForwardedHeaders_Enabled"] = "false";   // forwarded headers are set up below, whatever ASPNETCORE_FORWARDEDHEADERS_ENABLED says on this machine
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");

        var s = b.Services;
        s.AddHttpContextAccessor();
        s.AddMetrics();
        s.AddSingleton(registry);
        s.AddSingleton(new Switches());                      // the hardened harness's overrides, all on
        s.AddSingleton(sw);
        s.AddSingleton(catalog);
        s.AddSingleton<SessionStore>();
        s.AddSingleton(new IdpEndpoint(idpBaseUrl));
        s.AddSingleton<SharedOidc>();
        s.AddSingleton<TokenFlow>();
        if (sw.Clock is not null) s.AddSingleton(sw.Clock);  // ahead of the library's TryAddSingleton(TimeProvider.System)

        s.AddAuthentication();

        // Handler services, registered once and bound to no scheme, exactly as in the real-handler harness.
        s.AddTransient<CookieAuthenticationHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        s.AddTransient<OpenIdConnectHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
        s.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptionsSetup>();
        s.AddSingleton<OptionsInvalidator>();
        s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());
        if (sw.StoreHonoursCancellation)
            s.PostConfigureAll<CookieAuthenticationOptions>(o => { if (o.SessionStore is { } store) o.SessionStore = new CancellationHonouringTicketStore(store); });

        s.AddAntiforgery(o =>
        {
            o.Cookie.Name = "__Host-spa-csrf";
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.SuppressReadingTokenFromFormBody = true;
        });
        s.AddSingleton<IAntiforgeryAdditionalDataProvider, AppBoundAntiforgeryData>();

        // ---- G0 item 4: Duende.AccessTokenManagement ----
        s.AddOpenIdConnectAccessTokenManagement(o =>
        {
            if (sw.Sentinel) o.ChallengeScheme = Scheme.Parse(SentinelScheme);
            if (sw.PostBodyClientAuth) o.ClientCredentialStyle = ClientCredentialStyle.PostBody;
        });

        // ---- G0 item 5: YARP ----
        var (routes, clusters) = ProxyConfig.Build(registry.Current, catalog);
        s.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .AddTransforms(t =>
            {
                // Upstream contract, "never sent upstream": the cookies, the CSRF header, the browser's own Authorization,
                // and forwarding headers the client supplied. These run before YARP adds its own X-Forwarded-* headers.
                t.AddRequestHeaderRemove("Cookie");
                t.AddRequestHeaderRemove("X-CSRF-TOKEN");
                if (!sw.YarpDefaultHeadersOnly)
                {
                    t.AddRequestTransform(rc =>
                    {
                        // NonValidated: enumerating Headers itself parses every header and rewrites some of the values on their way upstream.
                        foreach (var name in rc.ProxyRequest.Headers.NonValidated.Select(h => h.Key).Where(IsRemovedForwardingHeader).ToList()) rc.ProxyRequest.Headers.Remove(name);
                        return ValueTask.CompletedTask;
                    });
                }
                t.AddRequestTransform(async rc =>
                {
                    var ctx = rc.HttpContext;
                    var token = ctx.Items[TokenFlow.TokenKey] as string;
                    if (token is null && sw.TokenStep == TokenStep.Transform && ctx.User.Identity?.IsAuthenticated == true)
                    {
                        var flow = ctx.RequestServices.GetRequiredService<TokenFlow>();
                        (token, var code, _) = await flow.AcquireAsync(ctx, RequestApp.Resolve(ctx, registry)!);
                        if (token is null) ctx.Items[TokenFlow.RefusedKey] = code;
                    }
                    if (token is null)
                    {
                        // A status other than 200 tells YARP not to forward. Nothing goes upstream without the session's token.
                        ctx.Items.TryAdd(TokenFlow.RefusedKey, "unauthenticated");
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    rc.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                });

                // "Returned to the browser": minus Set-Cookie and Access-Control-*.
                t.AddResponseHeaderRemove("Set-Cookie", sw.StripOnSuccessOnly ? ResponseCondition.Success : ResponseCondition.Always);
                t.AddResponseTransform(rc =>
                {
                    var headers = rc.HttpContext.Response.Headers;
                    foreach (var name in headers.Keys.Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)).ToList()) headers.Remove(name);
                    return ValueTask.CompletedTask;
                });
            });

        var app = b.Build();
        app.Services.GetRequiredService<OptionsInvalidator>();
        app.Services.GetRequiredService<SharedOidc>().Backchannel.DefaultRequestHeaders.UserAgent.ParseAdd(BackchannelAgent);

        // Task 4.1: "replaced atomically when the registry swaps". This handler runs after the options eviction above.
        var proxyConfig = app.Services.GetRequiredService<InMemoryConfigProvider>();
        registry.Swapped += (_, next) =>
        {
            var (r, c) = ProxyConfig.Build(next, catalog);
            proxyConfig.Update(r, c);
        };

        // As behind the load balancer: its X-Forwarded-For and X-Forwarded-Proto are applied, one hop, from a known proxy.
        // Loopback is the known proxy by default, and the test client stands in for it.
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

        app.Use(async (ctx, next) =>
        {
            if (RequestApp.Resolve(ctx, registry) is null) { await Problem(ctx, 404, "not_found"); return; }

            // Stands in for the security-headers stage: set when the response starts, so the gateway's value replaces an upstream's.
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers.ContentSecurityPolicy = GatewayCsp;
                if (Activity.Current is { } activity) ctx.Response.Headers["X-Test-Trace-Id"] = activity.TraceId.ToString();
                return Task.CompletedTask;
            });

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
            await ctx.Response.WriteAsync($"shell app={RequestApp.Resolve(ctx, registry)!.AppId} sub={auth.Principal!.FindFirst("sub")?.Value}");
        });

        app.MapGet("/bff/login", async ctx =>
        {
            await ctx.SignOutAsync();
            await ctx.ChallengeAsync(new AuthenticationProperties { RedirectUri = "/" });
        });

        // Browser contract: "Revokes the refresh token, removes the session, then redirects through the provider's logout".
        app.MapPost("/bff/logout", async ctx =>
        {
            var entry = RequestApp.Resolve(ctx, registry)!;
            var flow = ctx.RequestServices.GetRequiredService<TokenFlow>();
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                if (sw.OwnOrchestration)
                {
                    await flow.EndAsync(ctx, entry);
                }
                else
                {
                    // The contract's order taken literally, on the library's own path: revoke, then remove the session. Probe B14 shows what that order costs.
                    try { await ctx.RevokeRefreshTokenAsync(flow.Parameters(entry, explicitSchemes: true)); }
                    catch (InvalidOperationException ex) { await Problem(ctx, 500, "revocation_failed", ex.Message); return; }
                }
            }
            await ctx.SignOutAsync(SchemeNames.Cookie(entry.AppId));
            await ctx.SignOutAsync(SchemeNames.Oidc(entry.AppId), new AuthenticationProperties { RedirectUri = "/" });
        });

        app.MapGet("/bff/session", (HttpContext ctx, IAntiforgery af) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Json(new { status = 401, code = "unauthenticated" }, statusCode: 401, contentType: "application/problem+json");
            var t = af.GetAndStoreTokens(ctx);
            return Results.Json(new { sub = ctx.User.FindFirst("sub")?.Value, csrfToken = t.RequestToken });
        });

        // Diagnostic endpoint, not part of the platform: the token call on its own, without the proxy.
        //   ?force=1       refresh whether or not the token is due
        //   ?schemes=none  leave ChallengeScheme and SignInScheme off the call
        //   ?end=0         report a refusal and leave the session alone
        app.MapGet("/bff/token", async (HttpContext ctx, TokenFlow flow) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Json(new { code = "unauthenticated" }, statusCode: 401);
            var q = ctx.Request.Query;
            try
            {
                var (token, code, detail) = await flow.AcquireAsync(ctx, RequestApp.Resolve(ctx, registry)!,
                    force: q["force"] == "1", explicitSchemes: q["schemes"] == "none" ? false : null, endOnRefusal: q["end"] != "0");
                return token is null
                    ? Results.Json(new { code, error = detail }, statusCode: code == "session_ended" && q["end"] == "0" ? 200 : TokenFlow.StatusFor(code))
                    : Results.Json(new { code, accessToken = token, error = detail });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { code = "exception", error = $"{ex.GetType().Name}: {ex.Message}" }, statusCode: 500);
            }
        });

        // Task 4.2: the proxy pipeline. This step runs ahead of YARP's forwarder.
        app.MapReverseProxy(proxy =>
        {
            proxy.Use(async (ctx, next) =>
            {
                var entry = RequestApp.Resolve(ctx, registry)!;
                var signedIn = ctx.User.Identity?.IsAuthenticated == true;
                if (sw.TokenStep == TokenStep.Pipeline && !signedIn) { await Problem(ctx, 401, "unauthenticated"); return; }

                // Task 4.4: unsafe methods carry a valid header token. Header only, so the body is still unread for YARP.
                if (signedIn && !await ctx.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(ctx)) { await Problem(ctx, 403, "csrf_failed"); return; }

                if (sw.TokenStep == TokenStep.Pipeline)
                {
                    var (token, code, detail) = await ctx.RequestServices.GetRequiredService<TokenFlow>().AcquireAsync(ctx, entry);
                    if (token is null) { await Problem(ctx, TokenFlow.StatusFor(code), code, detail); return; }
                    ctx.Items[TokenFlow.TokenKey] = token;
                }

                await next(ctx);

                // Nothing has been sent yet when YARP reports a failure before the upstream's first body byte, or when a transform refused.
                if (ctx.Response.HasStarted) return;
                if (ctx.GetForwarderErrorFeature() is { } failure)
                    await Problem(ctx, ctx.Response.StatusCode, ctx.Response.StatusCode == 504 ? "upstream_timeout" : "upstream_unavailable", failure.Error.ToString());
                else if (ctx.Items[TokenFlow.RefusedKey] is string refused)
                    await Problem(ctx, TokenFlow.StatusFor(refused), refused);
            });
        });

        // An API name the app does not list matches no proxy route and lands here, not on an asset.
        app.Map("/api/{**rest}", ctx => Problem(ctx, 404, "unknown_api"));

        return new ProxyGateway { App = app, BaseUrl = "", Apis = catalog };
    }

    public static async Task<ProxyGateway> StartAsync(Registry registry, string idpBaseUrl, TokenSwitches sw, ApiCatalog catalog)
        => await Build(registry, idpBaseUrl, sw, catalog).StartAsync();

    public async Task<ProxyGateway> StartAsync()
    {
        await App.StartAsync();
        return new ProxyGateway
        {
            App = App,
            Apis = Apis,
            BaseUrl = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(),
        };
    }
}
