using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The profile stub. Workstream 3 writes the two real profiles in task 3.7; this records what it was
/// asked to do so another workstream can assert the profile was consulted without depending on
/// either one.
///
/// Shares the G1b contract with <see cref="IIdpProfile"/> (ADR-0017).
/// </summary>
public sealed class StubIdpProfileTests
{
    [Fact]
    public void A_stub_is_the_standard_profile_unless_it_is_told_otherwise()
    {
        Assert.Equal("standard", new StubIdpProfile().Name);
    }

    [Fact]
    public void A_stub_can_stand_in_for_the_cognito_profile()
    {
        Assert.Equal("cognito", new StubIdpProfile("cognito").Name);
    }

    [Fact]
    public void Configuring_records_the_app_and_the_issuer_it_was_given()
    {
        StubIdpProfile profile = new();
        AppDefinition app = TestApps.Entry("app1");
        IssuerDefinition issuer = Issuer();

        profile.Configure(new OpenIdConnectOptions(), app, issuer);

        (AppDefinition App, IssuerDefinition Issuer) call = Assert.Single(profile.Configured);
        Assert.Same(app, call.App);
        Assert.Same(issuer, call.Issuer);
    }

    [Fact]
    public void Configuring_twice_records_both_calls_in_order()
    {
        // Two apps on one issuer. Task 3.2 builds options per scheme.
        StubIdpProfile profile = new();

        profile.Configure(new OpenIdConnectOptions(), TestApps.Entry("app1"), Issuer());
        profile.Configure(new OpenIdConnectOptions(), TestApps.Entry("app2"), Issuer());

        Assert.Equal(["app1", "app2"], profile.Configured.Select(call => call.App.AppId));
    }

    [Fact]
    public void A_stub_can_be_arranged_to_change_the_options_it_is_handed()
    {
        // So a test can prove the profile's contribution reached the built options.
        StubIdpProfile profile = new()
        {
            OnConfigure = (options, _, issuer) => options.Authority = issuer.Authority,
        };
        OpenIdConnectOptions options = new();

        profile.Configure(options, TestApps.Entry("app1"), Issuer());

        Assert.Equal("https://issuer.example", options.Authority);
    }

    [Fact]
    public async Task A_sign_out_redirect_records_the_app_and_the_issuer()
    {
        StubIdpProfile profile = new();
        AppDefinition app = TestApps.Entry("app1");
        IssuerDefinition issuer = Issuer();

        await profile.RedirectToSignOutAsync(RedirectContext(), app, issuer);

        (AppDefinition App, IssuerDefinition Issuer) call = Assert.Single(profile.SignOutRedirects);
        Assert.Same(app, call.App);
        Assert.Same(issuer, call.Issuer);
    }

    [Fact]
    public async Task A_stub_can_be_arranged_to_handle_the_sign_out_redirect()
    {
        // The Cognito profile lands the user on "/" rather than on the callback path (finding
        // R-C7). A test arranges that here without needing the real profile.
        StubIdpProfile profile = new()
        {
            OnSignOutRedirect = context =>
            {
                context.Response.Redirect("/");
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
        RedirectContext context = RedirectContext();

        await profile.RedirectToSignOutAsync(context, TestApps.Entry("app1"), Issuer());

        Assert.Equal("/", context.Response.Headers.Location);
    }

    private static IssuerDefinition Issuer() =>
        new("https://issuer.example", "standard", new Uri("https://login.issuer.example"));

    private static RedirectContext RedirectContext() =>
        new(
            new DefaultHttpContext(),
            new AuthenticationScheme(SchemeNames.Oidc("app1"), displayName: null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new AuthenticationProperties());
}
