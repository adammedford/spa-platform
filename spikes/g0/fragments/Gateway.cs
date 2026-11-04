using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Spike;

// PROTOTYPE. One shell entry with nested fragments, as "The fragment entry" decided (#770).

public sealed record FragmentEntry(string FragmentId, string ClientId, string ClientSecret, string Scope = "openid");

public sealed record ShellEntry(string AppId, string Host, string ClientId, string ClientSecret, IReadOnlyList<FragmentEntry> Fragments, string Scope = "openid")
{
    public FragmentEntry? Fragment(string id) => Fragments.FirstOrDefault(f => f.FragmentId == id);

    // Record equality on the list is reference equality; the invalidator compares the shell's own fields only.
    public (string, string, string, string, string) ShellFields => (AppId, Host, ClientId, ClientSecret, Scope);
}

public sealed class RegistrySnapshot
{
    public required int Version { get; init; }
    public required FrozenDictionary<string, ShellEntry> ByHost { get; init; }
    public required FrozenDictionary<string, ShellEntry> ByAppId { get; init; }
}

public sealed class Registry
{
    private readonly object _lock = new();
    private RegistrySnapshot _current = Build(0, []);
    public RegistrySnapshot Current => Volatile.Read(ref _current);
    public event Action<RegistrySnapshot, RegistrySnapshot>? Swapped;

    public void Publish(params ShellEntry[] apps)
    {
        lock (_lock)
        {
            var old = Current;
            var next = Build(old.Version + 1, apps);
            Volatile.Write(ref _current, next);
            Swapped?.Invoke(old, next);
        }
    }

    private static RegistrySnapshot Build(int version, IReadOnlyList<ShellEntry> apps)
    {
        var clients = apps.SelectMany(a => a.Fragments.Select(f => f.ClientId).Prepend(a.ClientId)).ToList();
        if (clients.Count != clients.Distinct(StringComparer.Ordinal).Count()) throw new InvalidOperationException("registry invalid: clientId shared");
        foreach (var a in apps)
        {
            if (a.Fragments.Select(f => f.FragmentId).Distinct(StringComparer.Ordinal).Count() != a.Fragments.Count) throw new InvalidOperationException("DuplicateFragmentId");
            if (a.AppId.Contains(':') || a.Fragments.Any(f => f.FragmentId.Contains(':') || f.FragmentId.Contains('/'))) throw new InvalidOperationException("bad id");
        }
        return new RegistrySnapshot
        {
            Version = version,
            ByHost = apps.ToFrozenDictionary(a => a.Host, StringComparer.OrdinalIgnoreCase),
            ByAppId = apps.ToFrozenDictionary(a => a.AppId, StringComparer.Ordinal),
        };
    }
}

public static class RequestApp
{
    private static readonly object Key = new();

    public static ShellEntry? Resolve(HttpContext ctx, Registry registry)
    {
        if (ctx.Items.TryGetValue(Key, out var cached)) return (ShellEntry?)cached;
        registry.Current.ByHost.TryGetValue(ctx.Request.Host.Value ?? "", out var app);
        ctx.Items[Key] = app;
        return app;
    }
}

/// <summary>The name form under test: oidc:{appId} for the shell, oidc:{appId}:{fragmentId} for a fragment.
/// Neither id may contain ':' (both follow the appId pattern), so the parse is unambiguous.</summary>
public static class SchemeNames
{
    public static string Cookie(string appId) => "cookie:" + appId;
    public static string Oidc(string appId) => "oidc:" + appId;
    public static string Oidc(string appId, string fragmentId) => $"oidc:{appId}:{fragmentId}";

    public static bool TryParseCookie(string? name, out string appId)
    {
        appId = name is not null && name.StartsWith("cookie:", StringComparison.Ordinal) ? name[7..] : "";
        return appId.Length > 0;
    }

    public static bool TryParseOidc(string? name, out string appId, out string? fragmentId)
    {
        appId = ""; fragmentId = null;
        if (name is null || !name.StartsWith("oidc:", StringComparison.Ordinal)) return false;
        var parts = name[5..].Split(':');
        if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length == 0)) return false;
        appId = parts[0];
        fragmentId = parts.Length == 2 ? parts[1] : null;
        return true;
    }
}

public sealed class Switches
{
    /// <summary>true: each fragment on /signin-oidc/{fragmentId}, exact dispatch. false: the control, every scheme on
    /// /signin-oidc with SkipUnrecognizedRequests, dispatched by which handler can unprotect the state.</summary>
    public bool PerFragmentPath = true;
    public bool PromptNoneOnChain = true;
}

public sealed class RegistrySchemeProvider(IHttpContextAccessor accessor, Registry registry) : IAuthenticationSchemeProvider
{
    private ShellEntry? App => accessor.HttpContext is { } ctx ? RequestApp.Resolve(ctx, registry) : null;

    private static AuthenticationScheme Cookie(ShellEntry a) => new(SchemeNames.Cookie(a.AppId), null, typeof(CookieAuthenticationHandler));
    private static AuthenticationScheme Oidc(string name) => new(name, null, typeof(OpenIdConnectHandler));
    private static IEnumerable<AuthenticationScheme> Remotes(ShellEntry a)
        => a.Fragments.Select(f => Oidc(SchemeNames.Oidc(a.AppId, f.FragmentId))).Prepend(Oidc(SchemeNames.Oidc(a.AppId)));
    private static Task<AuthenticationScheme?> T(AuthenticationScheme? s) => Task.FromResult(s);

    public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => T(App is { } a ? Cookie(a) : null);
    public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => T(App is { } a ? Oidc(SchemeNames.Oidc(a.AppId)) : null);

    public Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (App is not { } a) return T(null);
        if (name == SchemeNames.Cookie(a.AppId)) return T(Cookie(a));
        return T(Remotes(a).FirstOrDefault(s => s.Name == name));
    }

    /// <summary>Shell first, then fragments in registry order. The order only matters on a shared callback path.</summary>
    public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
        => Task.FromResult(App is { } a ? Remotes(a).ToList() : Enumerable.Empty<AuthenticationScheme>());

    public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
        => Task.FromResult(App is { } a ? Remotes(a).Prepend(Cookie(a)).ToList() : Enumerable.Empty<AuthenticationScheme>());

    public void AddScheme(AuthenticationScheme scheme) => throw new NotSupportedException();
    public void RemoveScheme(string name) => throw new NotSupportedException();
}

public sealed record IdpEndpoint(string BaseUrl);

public sealed class SharedOidc
{
    public HttpClient Backchannel { get; } = new();
    public IConfigurationManager<OpenIdConnectConfiguration> Metadata { get; }
    public SharedOidc(IdpEndpoint idp) => Metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
        idp.BaseUrl + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(Backchannel) { RequireHttps = false });
}

/// <summary>What the checks read: chain timings, merges, evictions. Holds no token values.</summary>
public sealed class GatewayLog
{
    public readonly ConcurrentQueue<(string FragmentId, string Outcome)> Merges = new();
    public readonly ConcurrentQueue<string> Evicted = new();
    public readonly ConcurrentQueue<(string Name, int Status)> Revocations = new();
    public readonly ConcurrentQueue<(int Step, long Ticks)> ChainSteps = new();
}

public static class Session
{
    // The cookie handler's session-id claim lives only in the cookie's own ticket, not in the stored one it
    // retrieves, so the store hands the key back in the properties.
    public const string SidItem = ".sid";
    public static async Task<string?> SidOf(HttpContext ctx)
        => (await ctx.AuthenticateAsync()) is { Succeeded: true } a && a.Properties!.Items.TryGetValue(SidItem, out var k) ? k : null;
    public static string State(string fragmentId) => ".Fragment." + fragmentId;
    public static string Token(string name, string fragmentId) => $".Token.{name}:{fragmentId}";
}

public sealed class SessionStore
{
    public sealed record Record(string AppId, byte[] Ticket);
    private readonly ConcurrentDictionary<string, Record> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public int Count => _items.Count;
    public IReadOnlyCollection<string> Keys => _items.Keys.ToList();
    public Record? TryGet(string key) => _items.TryGetValue(key, out var r) ? r : null;
    public void Set(string key, Record r) => _items[key] = r;
    public bool Remove(string key) => _items.TryRemove(key, out _);
    public AuthenticationTicket? Ticket(string key) => TryGet(key) is { } r ? TicketSerializer.Default.Deserialize(r.Ticket) : null;
    public SemaphoreSlim Gate(string key) => _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
}

public sealed class AppBoundTicketStore(string appId, SessionStore store) : ITicketStore
{
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        store.Set(key, new SessionStore.Record(appId, TicketSerializer.Default.Serialize(ticket)));
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (Owned(key)) store.Set(key, new SessionStore.Record(appId, TicketSerializer.Default.Serialize(ticket)));
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var ticket = Owned(key) ? store.Ticket(key) : null;
        if (ticket is not null) ticket.Properties.Items[Session.SidItem] = key;
        return Task.FromResult(ticket);
    }

    public Task RemoveAsync(string key)
    {
        if (Owned(key)) store.Remove(key);
        return Task.CompletedTask;
    }

    private bool Owned(string key) => store.TryGet(key)?.AppId == appId;
}

/// <summary>Hides the session cookie on the shell's callback so its sign-in always writes a new record.</summary>
public sealed class CallbackBlindCookieManager(ICookieManager inner) : ICookieManager
{
    public string? GetRequestCookie(HttpContext context, string key) => context.Request.Path == "/signin-oidc" ? null : inner.GetRequestCookie(context, key);
    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) => inner.AppendResponseCookie(context, key, value, options);
    public void DeleteCookie(HttpContext context, string key, CookieOptions options) => inner.DeleteCookie(context, key, options);
}

public sealed class CookieOptionsSetup(Registry registry, SessionStore store) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(CookieAuthenticationOptions options) { }

    public void Configure(string? name, CookieAuthenticationOptions o)
    {
        if (!SchemeNames.TryParseCookie(name, out var appId) || !registry.Current.ByAppId.ContainsKey(appId)) return;
        o.Cookie.Name = "__Host-spa-session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.SessionStore = new AppBoundTicketStore(appId, store);
        o.CookieManager = new CallbackBlindCookieManager(new ChunkingCookieManager());
    }
}

public sealed class OidcOptionsSetup(Registry registry, SharedOidc shared, Switches sw, SessionStore store, GatewayLog log) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) { }

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (!SchemeNames.TryParseOidc(name, out var appId, out var fid) || !registry.Current.ByAppId.TryGetValue(appId, out var app)) return;
        var fragment = fid is null ? null : app.Fragment(fid);
        if (fid is not null && fragment is null) return;

        o.ClientId = fragment?.ClientId ?? app.ClientId;
        o.ClientSecret = fragment?.ClientSecret ?? app.ClientSecret;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.ResponseMode = "query";
        o.UsePkce = true;
        o.SaveTokens = true;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        foreach (var s in (fragment?.Scope ?? app.Scope).Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(s);
        o.SignInScheme = SchemeNames.Cookie(app.AppId);
        o.Backchannel = shared.Backchannel;
        o.ConfigurationManager = shared.Metadata;
        o.TokenValidationParameters.NameClaimType = "sub";
        o.CorrelationCookie.Name = "__Host-spa-corr."; o.CorrelationCookie.Path = "/"; o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.Name = "__Host-spa-nonce."; o.NonceCookie.Path = "/"; o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.RemoteSignOutPath = default;

        if (fid is null)
        {
            o.CallbackPath = "/signin-oidc";
            o.SkipUnrecognizedRequests = !sw.PerFragmentPath;
            // The eager chain starts from the shell's stock sign-in: only where it lands changes.
            o.Events.OnTicketReceived = ctx =>
            {
                ctx.ReturnUri = "/bff/chain?i=0&returnUrl=" + Uri.EscapeDataString(ctx.ReturnUri ?? "/");
                return Task.CompletedTask;
            };
            return;
        }

        o.CallbackPath = sw.PerFragmentPath ? "/signin-oidc/" + fid : "/signin-oidc";
        o.SkipUnrecognizedRequests = !sw.PerFragmentPath;
        o.SignedOutCallbackPath = PathString.Empty;   // a fragment registers no logout URI; the shell's logout ends the record

        o.Events.OnRedirectToIdentityProvider = ctx =>
        {
            if (sw.PromptNoneOnChain && ctx.Properties.Items.ContainsKey("chain")) ctx.ProtocolMessage.Prompt = "none";
            return Task.CompletedTask;
        };

        // The merge: never the stock sign-in, which would write a whole new ticket and drop the shell's tokens.
        o.Events.OnTicketReceived = async ctx =>
        {
            var outcome = await MergeAsync(fid, ctx.Properties!, ctx.Principal!.FindFirst("sub")?.Value);
            log.Merges.Enqueue((fid, outcome));
            ctx.Response.Redirect(ctx.ReturnUri ?? "/");   // the handler moves RedirectUri to ReturnUri before this event
            ctx.HandleResponse();
        };

        // A failed hop with readable state: mark the fragment, carry on to the next hop or the return URL.
        o.Events.OnRemoteFailure = async ctx =>
        {
            if (ctx.Properties?.Items.TryGetValue("sid", out var sid) == true && sid is not null)
            {
                await MarkAsync(sid, fid, "unavailable");
                log.Merges.Enqueue((fid, "failed: " + Kind(ctx.Failure)));
                ctx.Response.Redirect(ctx.Properties.RedirectUri ?? "/");
            }
            else
            {
                log.Merges.Enqueue((fid, "failed, no state: " + Kind(ctx.Failure)));
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("login failed");
            }
            ctx.HandleResponse();
        };

        async Task<string> MergeAsync(string fragmentId, AuthenticationProperties props, string? sub)
        {
            if (!props.Items.TryGetValue("sid", out var sid) || sid is null) return "refused: no session";
            var gate = store.Gate(sid);
            await gate.WaitAsync();
            try
            {
                if (store.TryGet(sid) is not { } record || record.AppId != app.AppId || store.Ticket(sid) is not { } ticket) return "refused: no session";
                if (ticket.Principal.FindFirst("sub")?.Value != sub)
                {
                    ticket.Properties.Items[Session.State(fragmentId)] = "unavailable";
                    store.Set(sid, record with { Ticket = TicketSerializer.Default.Serialize(ticket) });
                    return "refused: subject mismatch";
                }
                foreach (var t in props.GetTokens()) ticket.Properties.Items[Session.Token(t.Name, fragmentId)] = t.Value;
                ticket.Properties.Items[Session.State(fragmentId)] = "signed-in";
                store.Set(sid, record with { Ticket = TicketSerializer.Default.Serialize(ticket) });
                return "merged";
            }
            finally { gate.Release(); }
        }

        async Task MarkAsync(string sid, string fragmentId, string state)
        {
            var gate = store.Gate(sid);
            await gate.WaitAsync();
            try
            {
                if (store.TryGet(sid) is { } record && store.Ticket(sid) is { } ticket)
                {
                    ticket.Properties.Items[Session.State(fragmentId)] = state;
                    store.Set(sid, record with { Ticket = TicketSerializer.Default.Serialize(ticket) });
                }
            }
            finally { gate.Release(); }
        }
    }

    private static string Kind(Exception? e) => e switch
    {
        null => "none",
        _ when e.Message.Contains("login_required", StringComparison.Ordinal) => "login_required",
        _ when e.Message.Contains("invalid_client", StringComparison.Ordinal) || e.Message.Contains("401", StringComparison.Ordinal) => "token endpoint refused the client",
        _ when e.Message.Contains("state", StringComparison.OrdinalIgnoreCase) => "state not recognised",
        _ => e.GetType().Name,
    };
}

/// <summary>Per-scheme eviction: a fragment's change evicts its own scheme name only; the shell's fields evict the shell's two.</summary>
public sealed class OptionsInvalidator
{
    public OptionsInvalidator(Registry registry, GatewayLog log,
        IOptionsMonitorCache<CookieAuthenticationOptions> cookies, IOptionsMonitorCache<OpenIdConnectOptions> oidc)
    {
        registry.Swapped += (old, next) =>
        {
            foreach (var appId in old.ByAppId.Keys.Union(next.ByAppId.Keys))
            {
                old.ByAppId.TryGetValue(appId, out var before);
                next.ByAppId.TryGetValue(appId, out var after);
                if (before?.ShellFields != after?.ShellFields)
                {
                    Evict(cookies, SchemeNames.Cookie(appId));
                    Evict(oidc, SchemeNames.Oidc(appId));
                }
                var ids = (before?.Fragments ?? []).Concat(after?.Fragments ?? []).Select(f => f.FragmentId).Distinct();
                foreach (var id in ids)
                    if (before?.Fragment(id) != after?.Fragment(id)) Evict(oidc, SchemeNames.Oidc(appId, id));
            }
        };

        void Evict<T>(IOptionsMonitorCache<T> cache, string name) where T : class
        {
            cache.TryRemove(name);
            log.Evicted.Enqueue(name);
        }
    }
}

public sealed class Gateway : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required string BaseUrl { get; init; }
    public SessionStore Sessions => App.Services.GetRequiredService<SessionStore>();
    public GatewayLog Log => App.Services.GetRequiredService<GatewayLog>();
    public ValueTask DisposeAsync() => App.DisposeAsync();

    public static async Task<Gateway> StartAsync(Registry registry, string idpBaseUrl, Switches sw)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        b.Configuration["ForwardedHeaders_Enabled"] = "false";
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");

        var s = b.Services;
        s.AddHttpContextAccessor();
        s.AddSingleton(registry);
        s.AddSingleton(sw);
        s.AddSingleton<SessionStore>();
        s.AddSingleton<GatewayLog>();
        s.AddSingleton(new IdpEndpoint(idpBaseUrl));
        s.AddSingleton<SharedOidc>();
        s.AddAuthentication();
        s.AddTransient<CookieAuthenticationHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        s.AddTransient<OpenIdConnectHandler>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        s.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, CookieOptionsSetup>();
        s.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptionsSetup>();
        s.AddSingleton<OptionsInvalidator>();
        s.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, RegistrySchemeProvider>());

        var app = b.Build();
        app.Services.GetRequiredService<OptionsInvalidator>();
        var log = app.Services.GetRequiredService<GatewayLog>();
        var store = app.Services.GetRequiredService<SessionStore>();
        var shared = app.Services.GetRequiredService<SharedOidc>();

        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });
        app.Use(async (ctx, next) =>
        {
            if (RequestApp.Resolve(ctx, registry) is null) { ctx.Response.StatusCode = 404; return; }
            try { await next(ctx); }
            catch (AuthenticationFailureException)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("login failed");
            }
        });
        app.UseAuthentication();

        static string Local(string? url) => url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal) ? u : "/";

        app.MapGet("/app", async ctx =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) { await ctx.ChallengeAsync(new AuthenticationProperties { RedirectUri = "/app" }); return; }
            await ctx.Response.WriteAsync("shell document");
        });

        app.MapGet("/bff/login", async ctx =>
        {
            var entry = RequestApp.Resolve(ctx, registry)!;
            var returnUrl = Local(ctx.Request.Query["returnUrl"]);
            if (ctx.Request.Query["fragment"].ToString() is { Length: > 0 } fid)
            {
                // The fallback: one fragment's flow into the live record. No discard of the shell's login.
                if (entry.Fragment(fid) is null) { ctx.Response.StatusCode = 404; return; }
                if (await Session.SidOf(ctx) is not { } sid) { ctx.Response.StatusCode = 401; return; }
                var props = new AuthenticationProperties { RedirectUri = returnUrl };
                props.Items["sid"] = sid;
                await ctx.ChallengeAsync(SchemeNames.Oidc(entry.AppId, fid), props);
                return;
            }
            await ctx.SignOutAsync();
            await ctx.ChallengeAsync(new AuthenticationProperties { RedirectUri = returnUrl });
        });

        // One hop of the eager chain per request: challenge fragment i, whose callback comes back to i+1.
        app.MapGet("/bff/chain", async ctx =>
        {
            var entry = RequestApp.Resolve(ctx, registry)!;
            var returnUrl = Local(ctx.Request.Query["returnUrl"]);
            var i = int.TryParse(ctx.Request.Query["i"], out var n) ? n : 0;
            log.ChainSteps.Enqueue((i, Stopwatch.GetTimestamp()));
            if (await Session.SidOf(ctx) is not { } sid || i >= entry.Fragments.Count) { ctx.Response.Redirect(returnUrl); return; }
            var props = new AuthenticationProperties { RedirectUri = $"/bff/chain?i={i + 1}&returnUrl={Uri.EscapeDataString(returnUrl)}" };
            props.Items["sid"] = sid;      // rides inside the protected state, never in clear
            props.Items["chain"] = "1";
            await ctx.ChallengeAsync(SchemeNames.Oidc(entry.AppId, entry.Fragments[i].FragmentId), props);
        });

        app.MapGet("/bff/session", async ctx =>
        {
            var auth = await ctx.AuthenticateAsync();
            if (!auth.Succeeded) { ctx.Response.StatusCode = 401; return; }
            var entry = RequestApp.Resolve(ctx, registry)!;
            var fragments = entry.Fragments.ToDictionary(f => f.FragmentId,
                f => auth.Properties!.Items.TryGetValue(Session.State(f.FragmentId), out var v) ? v : "missing");
            await ctx.Response.WriteAsJsonAsync(new { sub = auth.Principal!.FindFirst("sub")?.Value, fragments });
        });

        // Revoke every set with its own client's credentials, then one provider logout under the shell's ID token.
        app.MapPost("/bff/logout", async ctx =>
        {
            var entry = RequestApp.Resolve(ctx, registry)!;
            var auth = await ctx.AuthenticateAsync();
            if (auth.Succeeded)
            {
                var items = auth.Properties!.Items;
                var config = await shared.Metadata.GetConfigurationAsync(ctx.RequestAborted);
                var sets = entry.Fragments.Select(f => (Name: f.FragmentId, f.ClientId, f.ClientSecret, Key: Session.Token("refresh_token", f.FragmentId)))
                    .Prepend((Name: "shell", entry.ClientId, entry.ClientSecret, Key: ".Token.refresh_token"));
                foreach (var set in sets)
                {
                    if (!items.TryGetValue(set.Key, out var rt) || rt is null) continue;
                    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["token"] = rt, ["token_type_hint"] = "refresh_token", ["client_id"] = set.ClientId, ["client_secret"] = set.ClientSecret,
                    });
                    var resp = await shared.Backchannel.PostAsync(config.RevocationEndpoint, form);
                    log.Revocations.Enqueue((set.Name, (int)resp.StatusCode));
                }
            }
            await ctx.SignOutAsync(SchemeNames.Oidc(entry.AppId), new AuthenticationProperties { RedirectUri = "/" });   // reads the ID token while the record exists
            await ctx.SignOutAsync(SchemeNames.Cookie(entry.AppId));                                                    // removes the record
        });

        app.MapFallback(ctx => { ctx.Response.StatusCode = 404; return ctx.Response.WriteAsync("path.reserved"); });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Gateway { App = app, BaseUrl = url };
    }
}
