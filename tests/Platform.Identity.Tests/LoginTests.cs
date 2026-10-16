using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.2's options through a whole login against the test identity provider: challenge, authorize,
/// callback, code exchange and sign-in to the app's cookie scheme. The cookie options are still the
/// framework's (task 3.3 sets them), which is enough for the login to complete.
/// </summary>
public sealed class LoginTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task Login_completes_with_host_prefixed_login_state_cookies_on_the_root_path()
    {
        // R-D4. A __Host- cookie is only accepted by a browser when it is Secure, on Path=/ and has no
        // Domain, which is what stops a sibling host planting it (R-D3).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        SetCookieHeaderValue[] loginState = [.. login.Challenge.Cookies];
        Assert.Equal(2, loginState.Length);
        Assert.Single(loginState, c => c.Name.Value!.StartsWith("__Host-spa-corr.", StringComparison.Ordinal));
        Assert.Single(loginState, c => c.Name.Value!.StartsWith("__Host-spa-nonce.", StringComparison.Ordinal));
        Assert.All(loginState, c =>
        {
            Assert.Equal("/", c.Path.Value);
            Assert.True(c.Secure);
            Assert.True(c.HttpOnly);
            Assert.False(c.Domain.HasValue);
            Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, c.SameSite);
        });

        // The callback completed: the code was exchanged with the preloaded secret, the app's cookie
        // scheme signed in, and both login-state cookies were deleted on the same path.
        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/", login.Callback.Location);
        Assert.Single(gateway.Idp.Grants, g => g.GrantType == "authorization_code" && g.ClientId == "client-app1");
        Assert.All(loginState, c => Assert.Contains(login.Callback.Cookies, d =>
            d.Name == c.Name && d.Path == "/" && d.Expires < DateTimeOffset.UtcNow));
        Assert.False(string.IsNullOrEmpty(login.Callback.SessionCookie.Value.Value));
    }

    [Fact]
    public async Task Discovery_and_signing_keys_are_fetched_once_for_both_apps()
    {
        // R-A7
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        await gateway.LoginAsync(App1);
        await gateway.LoginAsync(App2);

        Assert.Equal(1, gateway.Idp.DiscoveryFetches);
        Assert.Equal(1, gateway.Idp.JwksFetches);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task The_remote_sign_out_path_is_not_claimed_and_the_session_survives(string method)
    {
        // R-D2 and N-27. The framework claims /signout-oidc by default and signs the user out there
        // with no token and no origin check (R-D1). Cleared, the request falls through to 404.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        SetCookieHeaderValue session = login.Callback.SessionCookie;
        Dictionary<string, string> jar = new() { [session.Name.Value!] = session.Value.Value! };

        IdentityGateway.Answer signOut = await gateway.SendAsync(App1, "/signout-oidc", jar, method);

        Assert.Equal(404, signOut.Status);
        Assert.Empty(signOut.Cookies);
        Assert.Equal(200, (await gateway.SendAsync(App1, "/", jar)).Status);
    }
}
