using System.Net;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.7's profiles at logout, through the services <c>AddPlatformIdentity</c> registers and the test
/// identity provider, through <c>POST /bff/logout</c> (task 3.9); what is under test is where each
/// profile then sends the browser.
/// </summary>
public sealed class LogoutTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task Standard_logout_removes_the_session_and_returns_through_the_end_session_endpoint_to_the_root()
    {
        // R-C6: the provider's end-session endpoint from discovery, with id_token_hint and state, then
        // back through the signed-out callback to /.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], stubProfile: false);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST");

        Assert.Equal(302, logout.Status);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
        IReadOnlyDictionary<string, string> sent = logout.RedirectQuery;
        Assert.False(string.IsNullOrEmpty(sent.GetValueOrDefault("id_token_hint")));
        Assert.False(string.IsNullOrEmpty(sent.GetValueOrDefault("state")));
        Assert.Equal($"https://{App1.Host}/signout-callback-oidc", sent.GetValueOrDefault("post_logout_redirect_uri"));

        Uri back = await FollowAsync(logout.Location);
        Logout recorded = Assert.Single(gateway.Idp.Logouts);
        Assert.Equal(EndSessionStyle.Standard, recorded.Style);
        Assert.Equal(App1.OAuth.ClientId, recorded.ClientId);
        Assert.True(recorded.Ok);

        IdentityGateway.Answer landed = await gateway.SendAsync(App1, back.PathAndQuery);
        Assert.Equal(302, landed.Status);
        Assert.Equal("/", landed.Location);
    }

    [Fact]
    public async Task Cognito_logout_removes_the_session_and_lands_on_the_root_by_client_id_and_logout_uri()
    {
        // The cognito profile: the logout endpoint on the issuer's browser origin, with client_id and
        // logout_uri, landing on / rather than on the signed-out callback (R-C7). The provider here
        // advertises no end_session_endpoint, so nothing of this comes from discovery.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], issuerProfile: "cognito", stubProfile: false);
        gateway.Idp.EndSessionStyle = EndSessionStyle.Cognito;
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST");

        Assert.Equal(302, logout.Status);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Equal(gateway.Idp.CognitoLogoutEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["client_id"] = App1.OAuth.ClientId,
                ["logout_uri"] = $"https://{App1.Host}/",
            },
            logout.RedirectQuery);

        Uri back = await FollowAsync(logout.Location);
        Assert.Equal($"https://{App1.Host}/", back.ToString());
        Logout recorded = Assert.Single(gateway.Idp.Logouts);
        Assert.Equal(EndSessionStyle.Cognito, recorded.Style);
        Assert.Equal(App1.OAuth.ClientId, recorded.ClientId);
        Assert.True(recorded.Ok);
    }

    [Fact]
    public async Task The_signed_out_callback_without_state_answers_200_with_an_empty_body()
    {
        // R-C7: what Cognito's logout would land on if it returned to the callback, since it sends no
        // state back. The handler answers an empty page, which is why the cognito profile lands on /.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], issuerProfile: "cognito", stubProfile: false);

        IdentityGateway.Answer landed = await gateway.SendAsync(App1, "/signout-callback-oidc");

        Assert.Equal(200, landed.Status);
        Assert.Equal("", landed.Location);
        Assert.Equal("", landed.Body);
    }

    [Fact]
    public async Task The_issuers_profile_sends_the_browser_to_logout_for_the_app_and_the_issuer()
    {
        // ADR-0011: the profile, chosen by the issuer, owns the redirect to the provider's logout.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        gateway.Profile.OnSignOutRedirect = context =>
        {
            context.Response.Redirect("https://logout.example.test/bye");
            context.HandleResponse();
            return Task.CompletedTask;
        };
        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST");

        Assert.Equal("https://logout.example.test/bye", logout.Location);
        (AppDefinition app, IssuerDefinition issuer) = Assert.Single(gateway.Profile.SignOutRedirects);
        Assert.Equal("app1", app.AppId);
        Assert.Equal(new IssuerDefinition(gateway.Idp.BaseUrl, "standard", new Uri(gateway.Idp.BaseUrl)), issuer);
    }

    /// <summary>The provider's answer to the browser: where it redirected.</summary>
    private static async Task<Uri> FollowAsync(string location)
    {
        using HttpClient browser = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage answer = await browser.GetAsync(new Uri(location), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return answer.Headers.Location!;
    }
}
