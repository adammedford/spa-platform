using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.2's challenge, through the framework's own handler on a pinned host, read off the redirect
/// to the test identity provider's authorize endpoint.
/// </summary>
public sealed class ChallengeTests
{
    private static readonly AppDefinition App1 = TestApps.Entry(
        "app1", issuer: IdentityGateway.IssuerName, scopes: ["openid", "profile", "https://orders.spa-poc.test/read"],
        resource: "https://orders.spa-poc.test");

    private static readonly AppDefinition App2 = TestApps.Entry(
        "app2", issuer: IdentityGateway.IssuerName, scopes: ["openid", "email"]);

    [Fact]
    public async Task App1s_host_challenges_with_client_1_and_its_own_redirect_uri()
    {
        // S-A1
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Answer challenge = await gateway.SendAsync(App1, "/");

        Assert.Equal(302, challenge.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint + "?", challenge.Location, StringComparison.Ordinal);
        Assert.Equal("client-app1", challenge.RedirectQuery["client_id"]);
        Assert.Equal("https://app1.spa-poc.test/signin-oidc", challenge.RedirectQuery["redirect_uri"]);
    }

    [Fact]
    public async Task App2s_host_challenges_with_client_2_on_the_same_callback_path()
    {
        // S-A2 and R-A2
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Answer challenge = await gateway.SendAsync(App2, "/");

        Assert.Equal(302, challenge.Status);
        Assert.Equal("client-app2", challenge.RedirectQuery["client_id"]);
        Assert.Equal("https://app2.spa-poc.test/signin-oidc", challenge.RedirectQuery["redirect_uri"]);
    }

    [Fact]
    public async Task App1s_challenge_carries_code_flow_pkce_nonce_its_scopes_and_resource_and_no_response_mode()
    {
        // R-A1. No response_mode: the handler leaves "query" out of the request, which is what
        // ResponseMode = query looks like on the wire (R-D8 is the form_post default this avoids).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IReadOnlyDictionary<string, string> query = (await gateway.SendAsync(App1, "/")).RedirectQuery;

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.Equal("openid profile https://orders.spa-poc.test/read", query["scope"]);
        Assert.Equal("https://orders.spa-poc.test", query["resource"]);
        Assert.False(query.ContainsKey("response_mode"));
    }

    [Fact]
    public async Task An_app_with_no_resource_binding_sends_no_resource_and_only_its_own_scopes()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IReadOnlyDictionary<string, string> query = (await gateway.SendAsync(App2, "/")).RedirectQuery;

        Assert.Equal("openid email", query["scope"]);
        Assert.False(query.ContainsKey("resource"));
    }
}
