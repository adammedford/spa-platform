using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.1 through the framework's own <see cref="AuthenticationMiddleware"/>, which is what
/// <c>UseAuthentication</c> installs: it asks the scheme provider for every request-handling scheme and
/// builds a handler for each (ADR-0001, Context). The seam is the handlers that middleware obtains
/// from <see cref="IAuthenticationHandlerProvider"/> on one request, over the services
/// <c>AddPlatformIdentity</c> registers. <see cref="SettableAppFeature"/> stands in for host
/// resolution, which runs before authentication.
/// </summary>
public sealed class HandlersPerRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exactly_one_openid_connect_handler_is_built_per_request_and_it_is_the_hosts()
    {
        // S-A7 (one remote handler per request) and R-A6 (one real OpenIdConnectHandler per request).
        AppDefinition[] apps = [TestApps.Entry("app1"), TestApps.Entry("app2")];
        await using Gateway gateway = await Gateway.StartAsync(apps);

        foreach (AppDefinition app in apps)
        {
            Built built = await gateway.SendAsync(app, "/");

            OpenIdConnectHandler oidc = Assert.Single(built.Handlers.Select(h => h.Handler).OfType<OpenIdConnectHandler>());
            Assert.Equal(SchemeNames.Oidc(app.AppId), oidc.Scheme.Name);
            Assert.Equal(200, built.Status);
        }
    }

    [Fact]
    public async Task With_303_apps_each_host_still_builds_one_openid_connect_handler_and_only_its_own_schemes()
    {
        // S-F1: the stock provider with every app's schemes built 303 handlers per request at 303 apps;
        // the host-scoped provider builds one. The timing half of S-F1 (142 to 172 microseconds) is a
        // measurement, not a component test; the count is what drives it.
        AppDefinition[] apps = [.. Enumerable.Range(1, 303).Select(i => TestApps.Entry("app" + i))];
        await using Gateway gateway = await Gateway.StartAsync(apps);

        foreach (AppDefinition app in new[] { apps[0], apps[150], apps[302] })
        {
            Built built = await gateway.SendAsync(app, "/");

            Assert.All(built.Handlers, h => Assert.EndsWith(":" + app.AppId, h.Scheme, StringComparison.Ordinal));
            Assert.Single(built.Handlers.Select(h => h.Handler).OfType<OpenIdConnectHandler>());
            Assert.True(built.Handlers.Count <= 2, $"{built.Handlers.Count} handlers on one request");
        }
    }

    [Fact]
    public async Task Any_of_303_apps_is_challenged_through_its_own_openid_connect_scheme()
    {
        // S-F1, "any app can log in": every host's challenge goes to its own client's scheme. The
        // login itself needs the OIDC options of task 3.2.
        AppDefinition[] apps = [.. Enumerable.Range(1, 303).Select(i => TestApps.Entry("app" + i))];
        await using Gateway gateway = await Gateway.StartAsync(apps);
        IAuthenticationSchemeProvider schemes = gateway.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        foreach (AppDefinition app in apps)
        {
            gateway.Pin(app);
            Assert.Equal(SchemeNames.Oidc(app.AppId), (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        }
    }

    [Fact]
    public async Task The_request_is_authenticated_through_the_hosts_own_cookie_scheme()
    {
        AppDefinition app2 = TestApps.Entry("app2");
        await using Gateway gateway = await Gateway.StartAsync([TestApps.Entry("app1"), app2]);

        Built built = await gateway.SendAsync(app2, "/");

        CookieAuthenticationHandler cookie = Assert.Single(built.Handlers.Select(h => h.Handler).OfType<CookieAuthenticationHandler>());
        Assert.Equal("cookie:app2", cookie.Scheme.Name);
        Assert.False(built.User?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task On_app1s_host_the_framework_refuses_app2s_scheme_by_name()
    {
        // R-B6 through the framework's own IAuthenticationService, which resolves a named scheme's
        // handler through the scheme provider and throws when there is none
        // (https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Http/Authentication.Core/src/AuthenticationService.cs).
        // The app's own cookie scheme authenticates (with no result, no cookie being sent), so the
        // refusal is about the name, not about authentication being unavailable.
        AppDefinition app1 = TestApps.Entry("app1");
        await using Gateway gateway = await Gateway.StartAsync([app1, TestApps.Entry("app2")]);
        using IServiceScope scope = gateway.Services.CreateScope();
        HttpContext context = gateway.Pin(app1);
        context.RequestServices = scope.ServiceProvider;
        context.Request.Scheme = "https";

        Assert.True((await context.AuthenticateAsync("cookie:app1")).None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.AuthenticateAsync("cookie:app2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ChallengeAsync("oidc:app2"));
    }

    private sealed record Handed(string Scheme, IAuthenticationHandler Handler);

    private sealed record Built(IReadOnlyList<Handed> Handlers, int Status, System.Security.Claims.ClaimsPrincipal? User);

    /// <summary>The handler provider the framework registers, recording every handler it hands out.</summary>
    private sealed class RecordingHandlerProvider(IAuthenticationSchemeProvider schemes) : IAuthenticationHandlerProvider
    {
        private readonly AuthenticationHandlerProvider inner = new(schemes);

        public List<Handed> Handed { get; } = [];

        public async Task<IAuthenticationHandler?> GetHandlerAsync(HttpContext context, string authenticationScheme)
        {
            IAuthenticationHandler? handler = await inner.GetHandlerAsync(context, authenticationScheme);
            if (handler is not null && !Handed.Any(h => ReferenceEquals(h.Handler, handler)))
            {
                Handed.Add(new Handed(authenticationScheme, handler));
            }

            return handler;
        }
    }

    /// <summary>
    /// <see cref="IdentityGateway"/> with the handler provider recording, and a request sent through
    /// the authentication middleware alone.
    /// </summary>
    private sealed class Gateway(IdentityGateway inner) : IAsyncDisposable
    {
        public static async Task<Gateway> StartAsync(AppDefinition[] apps) => new(await IdentityGateway.StartAsync(
            apps,
            services => services.Replace(ServiceDescriptor.Scoped<IAuthenticationHandlerProvider, RecordingHandlerProvider>())));

        public IServiceProvider Services => inner.Services;

        public HttpContext Pin(AppDefinition app) => inner.Pin(app);

        /// <summary>One request through <c>UseAuthentication</c>'s middleware, in its own scope.</summary>
        public async Task<Built> SendAsync(AppDefinition app, string path)
        {
            using IServiceScope scope = Services.CreateScope();
            HttpContext context = Pin(app);
            context.RequestServices = scope.ServiceProvider;
            context.Request.Method = "GET";
            context.Request.Path = path;

            ClaimsHolder seen = new();
            AuthenticationMiddleware middleware = new(
                next: c => { seen.User = c.User; c.Response.StatusCode = 200; return Task.CompletedTask; },
                schemes: Services.GetRequiredService<IAuthenticationSchemeProvider>());
            await middleware.Invoke(context).WaitAsync(Ct);

            RecordingHandlerProvider handlers = (RecordingHandlerProvider)scope.ServiceProvider
                .GetRequiredService<IAuthenticationHandlerProvider>();
            return new Built([.. handlers.Handed], context.Response.StatusCode, seen.User);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ClaimsHolder
    {
        public System.Security.Claims.ClaimsPrincipal? User { get; set; }
    }
}
