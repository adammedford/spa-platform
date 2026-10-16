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
/// The services <c>AddPlatformIdentity</c> registers, in front of the test identity provider, with
/// the framework's <c>UseAuthentication</c> as the only middleware. <see cref="SettableAppFeature"/>
/// stands in for host resolution, which runs before authentication and pins the app.
///
/// The terminal handler stands in for what later rows build: an anonymous request to <c>/</c> is
/// challenged (the shell gate of task 3.9), an anonymous request to <see cref="ApiPath"/> gets 401
/// and an authenticated one 200 (the API's answer, task 4.2), and every other path is 404, as routing
/// answers a path nothing claims. Platform configuration carries one issuer, <see cref="IssuerName"/>,
/// whose authority is the provider's loopback origin. Sessions go to <see cref="Sessions"/>, the
/// Platform.Testing store behind a recorder, and every request carries a
/// <see cref="SettableRequestTelemetryFeature"/> in place of <c>UsePlatformTelemetry</c>.
/// </summary>
internal sealed class IdentityGateway : IAsyncDisposable
{
    public const string IssuerName = "primary";

    /// <summary>A path standing in for an upstream API: 401 when anonymous, 200 when authenticated.</summary>
    public const string ApiPath = "/api/probe";

    private readonly IHost host;
    private readonly RequestDelegate pipeline;

    private IdentityGateway(
        TestIdentityProvider idp, IHost host, RegistrySnapshot snapshot, StubIdpProfile profile, RecordingSessionStore sessions)
    {
        Idp = idp;
        Sessions = sessions;
        this.host = host;
        Snapshot = snapshot;
        Profile = profile;

        ApplicationBuilder app = new(host.Services);
        app.UseAuthentication();
        app.Run(async context =>
        {
            bool authenticated = context.User.Identity?.IsAuthenticated == true;
            if (context.Request.Path == "/" && !authenticated)
            {
                await context.ChallengeAsync();
                return;
            }

            context.Response.StatusCode = context.Request.Path == "/" ? 200
                : context.Request.Path == ApiPath ? (authenticated ? 200 : 401)
                : 404;
        });
        pipeline = app.Build();
    }

    public TestIdentityProvider Idp { get; }

    public RegistrySnapshot Snapshot { get; }

    public StubIdpProfile Profile { get; }

    /// <summary>Every session the gateway stored, keyed by session key.</summary>
    public RecordingSessionStore Sessions { get; }

    public IServiceProvider Services => host.Services;

    /// <summary>The secret the provider and the fake secret source agree on for an app.</summary>
    public static string SecretOf(AppDefinition app) => "secret-" + app.AppId;

    /// <summary>The redirect URI the handler builds on an app's host: one callback path for every app.</summary>
    public static string RedirectUriOf(AppDefinition app) => $"https://{app.Host}/signin-oidc";

    public static async Task<IdentityGateway> StartAsync(
        IEnumerable<AppDefinition> apps,
        Action<IServiceCollection>? services = null)
    {
        AppDefinition[] entries = [.. apps];
        TestIdentityProvider idp = new();
        await idp.StartAsync(TestContext.Current.CancellationToken);

        FakeClientSecretSource secrets = new();
        foreach (AppDefinition app in entries)
        {
            idp.AddClient(app.OAuth.ClientId, SecretOf(app), RedirectUriOf(app), $"https://{app.Host}/");
            secrets[app.OAuth.ClientSecretRef] = SecretOf(app);
        }

        await secrets.PreloadAsync(entries.Select(a => a.OAuth.ClientSecretRef), TestContext.Current.CancellationToken);

        RegistrySnapshot snapshot = InMemoryRegistry.SnapshotOf(1, "\"r1\"", entries);
        StubIdpProfile profile = new("standard");

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Issuers:{IssuerName}:Authority"] = idp.BaseUrl,
            [$"Issuers:{IssuerName}:Profile"] = "standard",
            [$"Issuers:{IssuerName}:BrowserOrigin"] = idp.BaseUrl,
        });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IRegistry>(new InMemoryRegistry(snapshot));
        builder.Services.AddSingleton<IClientSecretSource>(secrets);
        builder.Services.AddSingleton<IIdpProfile>(profile);
        RecordingSessionStore sessions = new(new InMemorySessionStore());
        builder.Services.AddSingleton<ISessionStore>(sessions);
        builder.AddPlatformIdentity();
        services?.Invoke(builder.Services);

        return new IdentityGateway(idp, builder.Build(), snapshot, profile, sessions);
    }

    /// <summary>Makes <paramref name="app"/> the pinned app of the current request, without sending one.</summary>
    public HttpContext Pin(AppDefinition app)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(app.Host);
        context.Features.Set<IAppFeature>(new SettableAppFeature(app, Snapshot));
        context.Features.Set<IRequestTelemetryFeature>(new SettableRequestTelemetryFeature());
        Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return context;
    }

    /// <summary>One request on <paramref name="app"/>'s host, in its own scope.</summary>
    public async Task<Answer> SendAsync(
        AppDefinition app,
        string pathAndQuery,
        IReadOnlyDictionary<string, string>? cookies = null,
        string method = "GET")
    {
        using IServiceScope scope = Services.CreateScope();
        HttpContext context = Pin(app);
        context.RequestServices = scope.ServiceProvider;
        context.Request.Method = method;

        int query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery[query..]);

        if (method == "POST")
        {
            // An empty form, as a cross-site form post would send.
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream();
        }

        if (cookies is { Count: > 0 })
        {
            context.Request.Headers.Cookie = string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
        }

        await pipeline(context).WaitAsync(TestContext.Current.CancellationToken);

        IList<SetCookieHeaderValue> set = SetCookieHeaderValue.ParseList([.. context.Response.Headers.SetCookie.Select(v => v ?? "")]);
        return new Answer(
            context.Response.StatusCode,
            context.Response.Headers.Location.ToString(),
            [.. set],
            context.User.FindFirst("sub")?.Value,
            context.Features.Get<IRequestTelemetryFeature>()!.SessionRef);
    }

    /// <summary>
    /// A whole login on <paramref name="app"/>'s host, as a browser would make it: the challenge, the
    /// provider's authorize endpoint, then the callback carrying the cookies the challenge set, and
    /// any <paramref name="planted"/> cookies besides.
    /// </summary>
    public async Task<Login> LoginAsync(AppDefinition app, IReadOnlyDictionary<string, string>? planted = null)
    {
        Authorized authorized = await AuthorizeAsync(app);
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
    public async Task<Authorized> AuthorizeAsync(AppDefinition app)
    {
        Answer challenge = await SendAsync(app, "/");
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
    /// authenticated user's <c>sub</c> if there was one, and the <c>session_ref</c> left on the request's
    /// telemetry feature.
    /// </summary>
    internal sealed record Answer(
        int Status, string Location, IReadOnlyList<SetCookieHeaderValue> Cookies, string? Subject, string? SessionRef)
    {
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
