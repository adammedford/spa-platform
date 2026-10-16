using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.2's options as the framework obtains them, by scheme name from
/// <see cref="IOptionsMonitor{TOptions}"/>, over the services <c>AddPlatformIdentity</c> registers.
/// </summary>
public sealed class OidcOptionsTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task An_openid_connect_scheme_named_for_no_app_is_refused_when_it_is_built()
    {
        // R-D9, through its fix: the framework validates only the scheme name AddOpenIdConnect was
        // given, so a dynamically named scheme would be handed out with no client id.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IOptionsMonitor<OpenIdConnectOptions> monitor = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        OptionsValidationException refused = Assert.Throws<OptionsValidationException>(() => monitor.Get(SchemeNames.Oidc("ghost")));

        Assert.Equal(SchemeNames.Oidc("ghost"), refused.OptionsName);
        Assert.Equal("client-app1", monitor.Get(SchemeNames.Oidc("app1")).ClientId);
    }

    [Fact]
    public async Task Every_app_on_an_issuer_shares_one_backchannel_and_one_metadata_manager()
    {
        // R-A7's mechanism. The fetch counts are in LoginTests.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IOptionsMonitor<OpenIdConnectOptions> monitor = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        OpenIdConnectOptions one = monitor.Get(SchemeNames.Oidc("app1"));
        OpenIdConnectOptions two = monitor.Get(SchemeNames.Oidc("app2"));

        Assert.NotNull(one.ConfigurationManager);
        Assert.Same(one.ConfigurationManager, two.ConfigurationManager);
        Assert.Same(one.Backchannel, two.Backchannel);
    }

    [Fact]
    public async Task The_options_carry_the_entrys_client_and_preloaded_secret_and_sign_in_to_its_own_cookie_scheme()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        OpenIdConnectOptions options = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc("app2"));

        Assert.Equal("client-app2", options.ClientId);
        Assert.Equal(IdentityGateway.SecretOf(App2), options.ClientSecret);
        Assert.Equal("cookie:app2", options.SignInScheme);
        Assert.True(options.SaveTokens);
        Assert.False(options.MapInboundClaims);
        Assert.False(options.RemoteSignOutPath.HasValue);
    }

    [Fact]
    public async Task The_issuers_profile_is_consulted_for_the_app_and_the_issuer_from_platform_configuration()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        gateway.Profile.OnConfigure = (options, _, _) => options.Prompt = "login";

        OpenIdConnectOptions options = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc("app1"));

        (AppDefinition app, IssuerDefinition issuer) = Assert.Single(gateway.Profile.Configured);
        Assert.Equal("app1", app.AppId);
        Assert.Equal(new IssuerDefinition(gateway.Idp.BaseUrl, "standard", new Uri(gateway.Idp.BaseUrl)), issuer);
        Assert.Equal("login", options.Prompt);
    }

    [Fact]
    public async Task Only_the_profile_the_issuer_names_is_consulted()
    {
        StubIdpProfile cognito = new("cognito");
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton<IIdpProfile>(cognito));

        gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SchemeNames.Oidc("app1"));

        Assert.Single(gateway.Profile.Configured);
        Assert.Empty(cognito.Configured);
    }

    [Fact]
    public async Task An_entry_naming_an_issuer_platform_configuration_lacks_fails_without_naming_its_secret()
    {
        AppDefinition stray = TestApps.Entry("app3", issuer: "elsewhere");
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, stray]);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SchemeNames.Oidc("app3")));

        Assert.Contains("'elsewhere'", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(IdentityGateway.SecretOf(stray), refused.Message, StringComparison.Ordinal);
    }
}
