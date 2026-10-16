using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.1: the scheme provider (ADR-0001). The seam is <see cref="IAuthenticationSchemeProvider"/>
/// as <c>AddPlatformIdentity</c> registers it, read on a request whose app host resolution has pinned
/// as <see cref="IAppFeature"/>. <see cref="SettableAppFeature"/> stands in for host resolution.
/// </summary>
public sealed class SchemeProviderTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1");
    private static readonly AppDefinition App2 = TestApps.Entry("app2");

    [Fact]
    public async Task On_an_apps_host_the_defaults_are_that_apps_scheme_pair()
    {
        using Gateway gateway = new();
        gateway.Request(App1, App2);

        Assert.Equal("oidc:app1", (await gateway.Schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal("cookie:app1", (await gateway.Schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal("cookie:app1", (await gateway.Schemes.GetDefaultSignInSchemeAsync())?.Name);
        Assert.Equal("cookie:app1", (await gateway.Schemes.GetDefaultSignOutSchemeAsync())?.Name);
        Assert.Equal("cookie:app1", (await gateway.Schemes.GetDefaultForbidSchemeAsync())?.Name);
    }

    [Fact]
    public async Task On_app1s_host_app2s_scheme_names_do_not_resolve()
    {
        // S-B8 (stand-in) and R-B6 (real handler): naming another app's scheme is the cross-client
        // attack ADR-0001 closes, so each name is asked for directly, not only as a default.
        using Gateway gateway = new();
        gateway.Request(App1, App1, App2);

        Assert.Null(await gateway.Schemes.GetSchemeAsync("oidc:app2"));
        Assert.Null(await gateway.Schemes.GetSchemeAsync("cookie:app2"));
        Assert.Equal("oidc:app1", (await gateway.Schemes.GetSchemeAsync("oidc:app1"))?.Name);
        Assert.Equal("cookie:app1", (await gateway.Schemes.GetSchemeAsync("cookie:app1"))?.Name);

        // The same names resolve on app2's own host, so the refusal above is about the host.
        gateway.Request(App2, App1, App2);
        Assert.Equal("oidc:app2", (await gateway.Schemes.GetSchemeAsync("oidc:app2"))?.Name);
        Assert.Null(await gateway.Schemes.GetSchemeAsync("oidc:app1"));
    }

    [Theory]
    [InlineData("Oidc:app1")]
    [InlineData("oidc:APP1")]
    [InlineData("oidc:app1 ")]
    [InlineData("Cookies")]
    [InlineData("OpenIdConnect")]
    [InlineData("")]
    public async Task A_name_that_is_not_exactly_the_apps_own_does_not_resolve(string name)
    {
        using Gateway gateway = new();
        gateway.Request(App1, App1);

        Assert.Null(await gateway.Schemes.GetSchemeAsync(name));
    }

    [Fact]
    public async Task On_an_apps_host_its_pair_is_every_scheme_and_its_oidc_scheme_the_only_request_handler()
    {
        using Gateway gateway = new();
        gateway.Request(App1, App1, App2);

        Assert.Equal(["cookie:app1", "oidc:app1"],
            (await gateway.Schemes.GetAllSchemesAsync()).Select(s => s.Name).Order(StringComparer.Ordinal));
        AuthenticationScheme handler = Assert.Single(await gateway.Schemes.GetRequestHandlerSchemesAsync());
        Assert.Equal("oidc:app1", handler.Name);
        Assert.Equal(typeof(OpenIdConnectHandler), handler.HandlerType);
        Assert.Equal(typeof(CookieAuthenticationHandler),
            (await gateway.Schemes.GetSchemeAsync("cookie:app1"))?.HandlerType);
    }

    [Fact]
    public async Task A_request_no_app_was_pinned_for_has_no_schemes()
    {
        using Gateway gateway = new();
        gateway.UnresolvedRequest();

        Assert.Null(await gateway.Schemes.GetDefaultChallengeSchemeAsync());
        Assert.Null(await gateway.Schemes.GetDefaultAuthenticateSchemeAsync());
        Assert.Null(await gateway.Schemes.GetDefaultSignInSchemeAsync());
        Assert.Null(await gateway.Schemes.GetDefaultSignOutSchemeAsync());
        Assert.Null(await gateway.Schemes.GetDefaultForbidSchemeAsync());
        Assert.Null(await gateway.Schemes.GetSchemeAsync("oidc:app1"));
        Assert.Empty(await gateway.Schemes.GetAllSchemesAsync());
        Assert.Empty(await gateway.Schemes.GetRequestHandlerSchemesAsync());
    }

    [Fact]
    public async Task Outside_a_request_there_are_no_schemes()
    {
        using Gateway gateway = new();

        Assert.Null(await gateway.Schemes.GetDefaultChallengeSchemeAsync());
        Assert.Empty(await gateway.Schemes.GetAllSchemesAsync());
    }

    [Fact]
    public async Task Adding_or_removing_a_scheme_is_unsupported()
    {
        using Gateway gateway = new();
        gateway.Request(App1, App1);

        Assert.Throws<NotSupportedException>(() => gateway.Schemes.AddScheme(
            new AuthenticationScheme("oidc:app2", null, typeof(OpenIdConnectHandler))));
        Assert.Throws<NotSupportedException>(() => gateway.Schemes.RemoveScheme("oidc:app1"));

        // TryAddScheme is a default interface member that calls AddScheme and reports false when it
        // throws; it must not become a way round the refusal.
        Assert.False(gateway.Schemes.TryAddScheme(
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler))));
        Assert.Null(await gateway.Schemes.GetSchemeAsync("oidc:app2"));
        Assert.Null(await gateway.Schemes.GetSchemeAsync("Cookies"));
        Assert.NotNull(await gateway.Schemes.GetSchemeAsync("oidc:app1"));
    }

    [Fact]
    public async Task Schemes_are_cached_per_snapshot()
    {
        using Gateway gateway = new();
        RegistrySnapshot first = InMemoryRegistry.SnapshotOf(1, "\"r1\"", [App1, App2]);
        RegistrySnapshot second = InMemoryRegistry.SnapshotOf(2, "\"r2\"", [App1, App2]);

        gateway.Request(App1, first);
        AuthenticationScheme? a = await gateway.Schemes.GetDefaultChallengeSchemeAsync();
        gateway.Request(App1, first);
        AuthenticationScheme? b = await gateway.Schemes.GetSchemeAsync("oidc:app1");
        gateway.Request(App1, second);
        AuthenticationScheme? c = await gateway.Schemes.GetDefaultChallengeSchemeAsync();

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(a.Name, c?.Name);
    }

    /// <summary>The gateway's identity services, and one request on one app's host at a time.</summary>
    private sealed class Gateway : IDisposable
    {
        private readonly IHost host;

        public Gateway()
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Services.AddLogging();
            builder.AddPlatformIdentity();
            host = builder.Build();
        }

        public IServiceProvider Services => host.Services;

        public IAuthenticationSchemeProvider Schemes => Services.GetRequiredService<IAuthenticationSchemeProvider>();

        /// <summary>Starts a request on <paramref name="app"/>'s host, against a snapshot of <paramref name="snapshotApps"/>.</summary>
        public HttpContext Request(AppDefinition app, params AppDefinition[] snapshotApps) =>
            Request(app, InMemoryRegistry.SnapshotOf(1, "\"r1\"", snapshotApps));

        public HttpContext Request(AppDefinition app, RegistrySnapshot snapshot)
        {
            DefaultHttpContext context = new() { RequestServices = Services };
            context.Request.Host = new HostString(app.Host);
            context.Features.Set<IAppFeature>(new SettableAppFeature(app, snapshot));
            Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
            return context;
        }

        /// <summary>A request that host resolution has not pinned: health, or an unknown host.</summary>
        public HttpContext UnresolvedRequest()
        {
            DefaultHttpContext context = new() { RequestServices = Services };
            Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
            return context;
        }

        public void Dispose() => host.Dispose();
    }
}
