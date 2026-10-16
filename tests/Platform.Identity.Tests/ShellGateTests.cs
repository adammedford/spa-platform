using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>UsePlatformShellGate</c> (task 3.9, ADR-0008): a session before any asset. An unauthenticated
/// navigation is challenged and an unauthenticated fetch gets 401; the paths that answer for their own
/// sessions are left to do so (contracts/request-gate.md -> Rules by path).
/// </summary>
public sealed class ShellGateTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task An_unauthenticated_navigation_to_an_asset_is_sent_to_the_provider_and_comes_back_to_it()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/orders/42?tab=open");

        Assert.Equal(App1.OAuth.ClientId, login.Challenge.RedirectQuery["client_id"]);
        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/orders/42?tab=open", login.Callback.Location);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task An_unauthenticated_fetch_for_an_asset_gets_401_with_a_problem_body_and_no_redirect(string method)
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer asset = await gateway.SendAsync(App1, "/assets/app.js", method: method, headers: IdentityGateway.Fetch);

        Assert.Equal(401, asset.Status);
        Assert.Equal("", asset.Location);
        Assert.Equal("application/problem+json", asset.ContentType);
        Assert.Empty(asset.Cookies);
        Assert.Empty(gateway.Idp.Authorizations);
    }

    [Fact]
    public async Task An_unauthenticated_fetch_gets_the_unauthenticated_code()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer asset = await gateway.SendAsync(App1, "/assets/app.js", headers: IdentityGateway.Fetch);

        Assert.Equal("unauthenticated", JsonDocument.Parse(asset.Body).RootElement.GetProperty("code").GetString());
        Assert.Null(asset.Rule);
    }

    [Fact]
    public async Task A_signed_in_request_passes_to_the_asset()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        IdentityGateway.Answer shell = await gateway.SendAsync(App1, "/", login.Jar, headers: IdentityGateway.Navigation);
        IdentityGateway.Answer script = await gateway.SendAsync(App1, "/assets/app.js", login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(200, shell.Status);
        Assert.Equal(gateway.Idp.DefaultSubject, shell.Subject);

        // The terminal stand-in answers 404 for every path it does not know, so 404 means it was reached.
        Assert.Equal(404, script.Status);
        Assert.Equal("", script.Location);
    }

    [Theory]
    [InlineData(IdentityGateway.ApiPath)]
    [InlineData("/API/probe")]
    [InlineData("/bff/unknown")]
    [InlineData("/signout-oidc")]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public async Task Paths_that_answer_for_their_own_sessions_are_not_challenged(string path)
    {
        // Each is reserved: no asset is served under it (contracts/http-contract.md), so passing it on
        // serves nothing without a session. /api answers its own 401 (task 4.2); here the stand-in does.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, path, headers: IdentityGateway.Navigation);

        Assert.Equal(path.Equals(IdentityGateway.ApiPath, StringComparison.OrdinalIgnoreCase) ? 401 : 404, answer.Status);
        Assert.Equal("", answer.Location);
        Assert.Empty(gateway.Idp.Authorizations);
    }

    [Fact]
    public async Task A_method_that_serves_no_asset_passes_on_to_the_stage_that_refuses_it()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer post = await gateway.SendAsync(App1, "/assets/app.js", method: "POST", headers: IdentityGateway.Navigation);

        Assert.Equal(404, post.Status);
        Assert.Equal("", post.Location);
    }

    [Fact]
    public async Task AddPlatformIdentity_alone_leaves_the_gate_out_of_the_pipeline()
    {
        // Deferred like the token source's binding, because the composed suites still request assets
        // without a session (PlatformIdentityExtensions.AddPlatformShellGate). Every other test in this
        // class runs with AddPlatformShellGate called, as IdentityGateway calls it.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IRegistry>(new InMemoryRegistry(InMemoryRegistry.SnapshotOf(1, "\"r1\"", [App1])));
        builder.Services.AddSingleton<IClientSecretSource>(new FakeClientSecretSource());
        builder.AddPlatformIdentity();
        using IHost host = builder.Build();

        ApplicationBuilder app = new(host.Services);
        app.UsePlatformShellGate();
        app.Run(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        DefaultHttpContext context = new() { RequestServices = host.Services };
        context.Request.Method = "GET";
        context.Request.Path = "/assets/app.js";
        await app.Build()(context);

        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_cookie_that_resolves_to_no_session_is_challenged_under_auth_session()
    {
        // contracts/request-gate.md: auth.session, "a cookie that resolves to no session for this app".
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        await gateway.Sessions.RemoveAsync(key, TestContext.Current.CancellationToken);

        IdentityGateway.Answer navigation = await gateway.SendAsync(App1, "/", login.Jar, headers: IdentityGateway.Navigation);
        IdentityGateway.Answer fetch = await gateway.SendAsync(App1, "/assets/app.js", login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(302, navigation.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, navigation.Location, StringComparison.Ordinal);
        Assert.Equal(RuleIds.AuthSession, navigation.Rule);
        Assert.Equal(401, fetch.Status);
        Assert.Equal(RuleIds.AuthSession, fetch.Rule);
    }
}
