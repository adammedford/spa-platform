using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// The services <c>AddPlatformIdentity</c> registers, in front of the test identity provider. Like
/// <c>Program.cs</c>, it registers nothing else of workstream 3's, so the shell gate and the token
/// source a test meets here are the ones the shipping gateway runs.
/// <see cref="SettableAppFeature"/> stands in for host resolution, which runs before authentication
/// and pins the app.
///
/// The pipeline follows contracts/pipeline.md: routing, the
/// framework's authentication, <c>UsePlatformShellGate</c> and <c>MapPlatformBff</c>. A terminal
/// handler stands in for what other workstreams build behind them: <c>/</c> answers 200 (an asset,
/// task 2.4), an anonymous request to <see cref="ApiPath"/> gets 401 and an authenticated one 200 (the
/// API's answer, task 4.2), and every other path is 404, as routing answers a path nothing claims.
/// <see cref="FakeCsrfTokens"/> stands in for the antiforgery tokens of task 4.4. Platform configuration carries one issuer, <see cref="IssuerName"/>,
/// whose authority is the provider's loopback origin. Sessions go to <see cref="Sessions"/>, the
/// Platform.Testing store behind a recorder, and every request carries a
/// <see cref="SettableRequestTelemetryFeature"/> in place of <c>UsePlatformTelemetry</c>.
/// </summary>
internal sealed class IdentityGateway : IAsyncDisposable
{
    public const string IssuerName = "primary";

    /// <summary>A path standing in for an upstream API: 401 when anonymous, 200 when authenticated.</summary>
    public const string ApiPath = "/api/probe";

    /// <summary><c>POST /bff/logout</c>, which hands the browser to the issuer's profile (task 3.7).</summary>
    public const string LogoutPath = "/bff/logout";

    /// <summary>
    /// A path standing in for the proxy's token step (task 4.2): it asks <see cref="IAccessTokenSource"/>
    /// for the session's token and answers 200 with the token as the body, or the outcome's status with
    /// its code from contracts/http-contract.md -> Errors as the body: 401 <c>unauthenticated</c>, 401
    /// <c>session_ended</c> or 503 <c>idp_unavailable</c>. An outcome it does not know throws, as the
    /// proxy's step refuses one, so no test reads an unknown outcome as a known one. It hands the source
    /// the request's own cancellation token on purpose, so a test can prove the source never lets a
    /// disconnecting browser cancel a refresh (T-B16).
    /// </summary>
    public const string TokenPath = "/api/token-probe";

    /// <summary>The fetch metadata a browser sends on a top-level navigation the user typed.</summary>
    public static readonly IReadOnlyDictionary<string, string> Navigation = new Dictionary<string, string>
    {
        ["Sec-Fetch-Site"] = "none",
        ["Sec-Fetch-Mode"] = "navigate",
        ["Sec-Fetch-Dest"] = "document",
    };

    /// <summary>The fetch metadata a browser sends on a same-origin <c>fetch()</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fetch = new Dictionary<string, string>
    {
        ["Sec-Fetch-Site"] = "same-origin",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
    };

    private readonly IHost host;
    private readonly RequestDelegate pipeline;

    private IdentityGateway(
        TestIdentityProvider idp,
        IHost host,
        InMemoryRegistry registry,
        FakeClientSecretSource secrets,
        StubIdpProfile profile,
        RecordingSessionStore sessions)
    {
        Idp = idp;
        Sessions = sessions;
        this.host = host;
        Registry = registry;
        Secrets = secrets;
        Profile = profile;

        ApplicationBuilder app = new(host.Services);
        app.UseRouting();
        app.UseAuthentication();
        app.UsePlatformShellGate();
        app.UseEndpoints(endpoints => endpoints.MapPlatformBff());
        app.Run(async context =>
        {
            if (context.Request.Path == TokenPath)
            {
                AccessTokenResult result = await context.RequestServices.GetRequiredService<IAccessTokenSource>()
                    .GetAsync(context, context.RequestAborted);
                (context.Response.StatusCode, string body) = result switch
                {
                    AccessTokenResult.Token token => (200, token.Value),
                    AccessTokenResult.Unauthenticated => (401, "unauthenticated"),
                    AccessTokenResult.SessionEnded => (401, "session_ended"),
                    AccessTokenResult.ProviderUnavailable => (503, "idp_unavailable"),
                    _ => throw new InvalidOperationException($"The probe knows no outcome {result.GetType().Name}."),
                };
                await context.Response.WriteAsync(body, CancellationToken.None);
                return;
            }

            bool authenticated = context.User.Identity?.IsAuthenticated == true;
            context.Response.StatusCode = context.Request.Path == "/" ? 200
                : context.Request.Path == ApiPath ? (authenticated ? 200 : 401)
                : 404;
        });
        pipeline = app.Build();
    }

    public TestIdentityProvider Idp { get; }

    /// <summary>The live snapshot: the one a request is pinned to unless it names another.</summary>
    public RegistrySnapshot Snapshot => Registry.Current;

    /// <summary>The registry, which <see cref="Swap"/> moves on as the registry poller of task 2.2 would.</summary>
    public InMemoryRegistry Registry { get; }

    /// <summary>The secret source the options setup reads; a test that rotates a secret sets and preloads it here.</summary>
    public FakeClientSecretSource Secrets { get; }

    public StubIdpProfile Profile { get; }

    /// <summary>Every session the gateway stored, keyed by session key.</summary>
    public RecordingSessionStore Sessions { get; }

    public IServiceProvider Services => host.Services;

    /// <summary>
    /// Returns once the session module has a gate held, which a due token's refresh takes before it
    /// reads the session and asks the provider, or fails if <paramref name="refresh"/> ends first: how a
    /// test sends a request while a refresh is in flight on the session.
    /// </summary>
    public async Task GateHeldAsync(Task refresh)
    {
        AppSessions sessions = Services.GetRequiredService<AppSessions>();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (sessions.ActiveGates == 0)
        {
            Assert.False(refresh.IsCompleted, "The refresh ended before it was seen holding the session's gate.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    /// <summary>The secret the provider and the fake secret source agree on for an app.</summary>
    public static string SecretOf(AppDefinition app) => "secret-" + app.AppId;

    /// <summary>The redirect URI the handler builds on an app's host: one callback path for every app.</summary>
    public static string RedirectUriOf(AppDefinition app) => $"https://{app.Host}/signin-oidc";

    /// <summary>
    /// The URIs the provider accepts after logout for an app: the handler's signed-out callback, where
    /// the standard profile returns (R-C6), and the app's root, where the cognito profile lands (R-C7).
    /// </summary>
    public static IReadOnlySet<string> PostLogoutUrisOf(AppDefinition app) =>
        new HashSet<string>(StringComparer.Ordinal) { $"https://{app.Host}/signout-callback-oidc", $"https://{app.Host}/" };

    private static void Register(TestIdentityProvider idp, AppDefinition app) =>
        idp.AddClient(new TestIdpClient(
            app.OAuth.ClientId,
            SecretOf(app),
            new HashSet<string>(StringComparer.Ordinal) { RedirectUriOf(app) },
            PostLogoutUrisOf(app)));

    /// <param name="apps">The registry's entries.</param>
    /// <param name="services">Registrations made after <c>AddPlatformIdentity</c>.</param>
    /// <param name="issuerProfile">What platform configuration names as the issuer's profile.</param>
    /// <param name="stubProfile">
    /// True to register <see cref="Profile"/> ahead of the real profiles, so it is the one consulted
    /// for <paramref name="issuerProfile"/>; false to leave the real profiles of task 3.7 in charge,
    /// with <see cref="Profile"/> registered under a name no issuer uses.
    /// </param>
    /// <param name="idpClock">
    /// The clock the provider runs its delays on: <see cref="HeldClock"/> for a test that acts while the
    /// provider holds a request. The system clock when null.
    /// </param>
    public static async Task<IdentityGateway> StartAsync(
        IEnumerable<AppDefinition> apps,
        Action<IServiceCollection>? services = null,
        string issuerProfile = "standard",
        bool stubProfile = true,
        TimeProvider? idpClock = null)
    {
        AppDefinition[] entries = [.. apps];
        TestIdentityProvider idp = new(idpClock);
        await idp.StartAsync(TestContext.Current.CancellationToken);

        FakeClientSecretSource secrets = new();
        foreach (AppDefinition app in entries)
        {
            Register(idp, app);
            secrets[app.OAuth.ClientSecretRef] = SecretOf(app);
        }

        await secrets.PreloadAsync(entries.Select(a => a.OAuth.ClientSecretRef), TestContext.Current.CancellationToken);

        InMemoryRegistry registry = new(InMemoryRegistry.SnapshotOf(1, "\"r1\"", entries));
        StubIdpProfile profile = new(stubProfile ? issuerProfile : "stub-unused");

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Issuers:{IssuerName}:Authority"] = idp.BaseUrl,
            [$"Issuers:{IssuerName}:Profile"] = issuerProfile,
            [$"Issuers:{IssuerName}:BrowserOrigin"] = idp.BaseUrl,
        });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IRegistry>(registry);
        builder.Services.AddSingleton<IClientSecretSource>(secrets);
        builder.Services.AddSingleton<IIdpProfile>(profile);
        RecordingSessionStore sessions = new(new InMemorySessionStore());
        builder.Services.AddSingleton<ISessionStore>(sessions);
        // What a web host registers and an empty host does not: routing's middleware needs both.
        builder.Services.AddRouting();
        builder.Services.AddSingleton(new System.Diagnostics.DiagnosticListener("Platform.Identity.Tests"));
        builder.Services.AddSingleton<System.Diagnostics.DiagnosticSource>(sp => sp.GetRequiredService<System.Diagnostics.DiagnosticListener>());
        builder.Services.AddSingleton<FakeCsrfTokens>();
        builder.Services.AddSingleton<ICsrfTokens>(sp => sp.GetRequiredService<FakeCsrfTokens>());
        builder.AddPlatformIdentity();
        services?.Invoke(builder.Services);

        return new IdentityGateway(idp, builder.Build(), registry, secrets, profile, sessions);
    }

    /// <summary>
    /// Swaps the registry onto a snapshot of <paramref name="apps"/>, one revision on, after registering
    /// at the provider and preloading the secret of every entry it has not seen. Returns the snapshot
    /// that was live before, for a test that sends a request still pinned to it.
    /// </summary>
    public async Task<RegistrySnapshot> SwapAsync(params AppDefinition[] apps)
    {
        foreach (AppDefinition app in apps.Where(a => !Idp.Clients.ContainsKey(a.OAuth.ClientId)))
        {
            Register(Idp, app);
            Secrets[app.OAuth.ClientSecretRef] = SecretOf(app);
        }

        await Secrets.PreloadAsync(apps.Select(a => a.OAuth.ClientSecretRef), TestContext.Current.CancellationToken);

        RegistrySnapshot previous = Registry.Current;
        Registry.Swap(InMemoryRegistry.SnapshotOf(previous.Revision + 1, $"\"r{previous.Revision + 1}\"", apps));
        return previous;
    }

    /// <summary>
    /// Makes <paramref name="app"/> the pinned app of the current request, without sending one. The
    /// request is pinned to <paramref name="snapshot"/>, or to the live snapshot when it names none.
    /// </summary>
    public HttpContext Pin(AppDefinition app, RegistrySnapshot? snapshot = null)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(app.Host);
        context.Features.Set<IAppFeature>(new SettableAppFeature(app, snapshot ?? Snapshot));
        context.Features.Set<IRequestTelemetryFeature>(new SettableRequestTelemetryFeature());
        Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return context;
    }

    /// <summary>
    /// One request on <paramref name="app"/>'s host, in its own scope, pinned to
    /// <paramref name="pinnedTo"/> or to the live snapshot. A host the pinned snapshot does not have
    /// gets 404 without reaching authentication, as <c>UsePlatformHostResolution</c> answers it.
    /// </summary>
    public async Task<Answer> SendAsync(
        AppDefinition app,
        string pathAndQuery,
        IReadOnlyDictionary<string, string>? cookies = null,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        RegistrySnapshot? pinnedTo = null,
        CancellationTokenSource? browser = null)
    {
        RegistrySnapshot snapshot = pinnedTo ?? Snapshot;
        if (!snapshot.ByHost.ContainsKey(app.Host))
        {
            return new Answer(404, "", [], null, null, null, null, "");
        }

        using IServiceScope scope = Services.CreateScope();
        HttpContext context = Pin(app, snapshot);
        context.RequestServices = scope.ServiceProvider;
        context.Request.Method = method;
        // The browser going away, when a test cancels this source: what the server's RequestAborted is.
        context.RequestAborted = browser?.Token ?? CancellationToken.None;

        int query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery[query..]);

        context.Response.Body = new MemoryStream();

        if (method == "POST")
        {
            // An empty form, as a cross-site form post would send.
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream();
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            context.Request.Headers[name] = value;
        }

        if (cookies is { Count: > 0 })
        {
            context.Request.Headers.Cookie = string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
        }

        await pipeline(context).WaitAsync(TestContext.Current.CancellationToken);

        IList<SetCookieHeaderValue> set = SetCookieHeaderValue.ParseList([.. context.Response.Headers.SetCookie.Select(v => v ?? "")]);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        return new Answer(
            context.Response.StatusCode,
            context.Response.Headers.Location.ToString(),
            [.. set],
            context.User.FindFirst("sub")?.Value,
            context.Features.Get<IRequestTelemetryFeature>()!.SessionRef,
            context.Features.Get<IRequestTelemetryFeature>()!.Rule,
            context.Response.ContentType,
            body)
        {
            CacheControl = context.Response.Headers.CacheControl.ToString(),
        };
    }

    /// <summary>
    /// A whole login on <paramref name="app"/>'s host, as a browser would make it: the challenge, the
    /// provider's authorize endpoint, then the callback carrying the cookies the challenge set, and
    /// any <paramref name="planted"/> cookies besides.
    /// </summary>
    /// <param name="start">The navigation that starts the login: <c>/</c> through the shell gate, or a
    /// <c>/bff/login</c> URL.</param>
    public async Task<Login> LoginAsync(AppDefinition app, IReadOnlyDictionary<string, string>? planted = null, string start = "/")
    {
        Authorized authorized = await AuthorizeAsync(app, start);
        Dictionary<string, string> jar = new(authorized.LoginState, StringComparer.Ordinal);
        foreach ((string name, string value) in planted ?? new Dictionary<string, string>())
        {
            jar[name] = value;
        }

        Answer callback = await SendAsync(app, authorized.Callback.PathAndQuery, jar);
        return new Login(authorized.Challenge, callback);
    }

    /// <summary>
    /// The first two legs of a login: the challenge on <paramref name="app"/>'s host and the provider's
    /// authorize endpoint. What comes back is the callback the provider redirected to, unsent, and the
    /// login-state cookies the challenge set.
    /// </summary>
    public async Task<Authorized> AuthorizeAsync(AppDefinition app, string start = "/", IReadOnlyDictionary<string, string>? cookies = null)
    {
        Answer challenge = await SendAsync(app, start, cookies, headers: Navigation);
        Assert.Equal(302, challenge.Status);

        using HttpClient browser = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage authorize = await browser.GetAsync(new Uri(challenge.Location), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);

        Uri back = authorize.Headers.Location!;
        Assert.Equal(RedirectUriOf(app), back.GetLeftPart(UriPartial.Path));

        Dictionary<string, string> jar = challenge.Cookies
            .Where(c => !c.Expires.HasValue || c.Expires > DateTimeOffset.UtcNow)
            .ToDictionary(c => c.Name.ToString(), c => c.Value.ToString() ?? "", StringComparer.Ordinal);
        return new Authorized(challenge, back, jar);
    }

    public async ValueTask DisposeAsync()
    {
        host.Dispose();
        await Idp.DisposeAsync();
    }

    /// <summary>
    /// What the gateway answered: the status, the <c>Location</c> header, every cookie it set, the
    /// authenticated user's <c>sub</c> if there was one, the <c>session_ref</c> and rule id left on the
    /// request's telemetry feature, and the body.
    /// </summary>
    internal sealed record Answer(
        int Status,
        string Location,
        IReadOnlyList<SetCookieHeaderValue> Cookies,
        string? Subject,
        string? SessionRef,
        string? Rule,
        string? ContentType,
        string Body)
    {
        /// <summary>The <c>Cache-Control</c> header, or empty.</summary>
        public string CacheControl { get; init; } = "";

        /// <summary>
        /// The one cookie set that is not login state: the app's session. Found by elimination so the
        /// tests do not depend on its name, which the cookie options of task 3.3 set.
        /// </summary>
        public SetCookieHeaderValue SessionCookie => Assert.Single(Cookies, c =>
            !c.Name.Value!.StartsWith("__Host-spa-corr.", StringComparison.Ordinal)
            && !c.Name.Value!.StartsWith("__Host-spa-nonce.", StringComparison.Ordinal));

        /// <summary>The query of the redirect, decoded.</summary>
        public IReadOnlyDictionary<string, string> RedirectQuery =>
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Location).Query)
                .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    internal sealed record Login(Answer Challenge, Answer Callback)
    {
        /// <summary>The session cookie the callback set, as a browser's jar would hold it.</summary>
        public Dictionary<string, string> Jar => new(StringComparer.Ordinal)
        {
            [Callback.SessionCookie.Name.Value!] = Callback.SessionCookie.Value.Value!,
        };
    }

    internal sealed record Authorized(Answer Challenge, Uri Callback, IReadOnlyDictionary<string, string> LoginState);
}
