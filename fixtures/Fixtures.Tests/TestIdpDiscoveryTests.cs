using System.Text.Json;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>Discovery and the key set: what a client reads before it does anything else.</summary>
public sealed class TestIdpDiscoveryTests
{
    [Fact]
    public async Task Discovery_names_every_endpoint_the_provider_serves()
    {
        await using var harness = await IdpHarness.StartAsync();

        var document = await harness.DiscoveryAsync();

        Assert.Equal(harness.Idp.BaseUrl, document.GetProperty("issuer").GetString());
        Assert.Equal(harness.Idp.AuthorizeEndpoint, document.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(harness.Idp.TokenEndpoint, document.GetProperty("token_endpoint").GetString());
        Assert.Equal(harness.Idp.JwksUri, document.GetProperty("jwks_uri").GetString());
        Assert.Equal(harness.Idp.RevocationEndpoint, document.GetProperty("revocation_endpoint").GetString());
        Assert.Equal(["authorization_code", "refresh_token"], Strings(document, "grant_types_supported"));
        Assert.Equal(["S256"], Strings(document, "code_challenge_methods_supported"));
        Assert.Equal(["RS256"], Strings(document, "id_token_signing_alg_values_supported"));
        Assert.Equal(["code"], Strings(document, "response_types_supported"));
        Assert.Equal(["client_secret_basic", "client_secret_post"], Strings(document, "token_endpoint_auth_methods_supported"));
        Assert.Equal(1, harness.Idp.DiscoveryFetches);
    }

    [Fact]
    public async Task Standard_style_advertises_end_session_and_Cognito_style_does_not()
    {
        await using var harness = await IdpHarness.StartAsync();

        var standard = await harness.DiscoveryAsync();
        Assert.Equal(harness.Idp.EndSessionEndpoint, standard.GetProperty("end_session_endpoint").GetString());

        harness.Idp.EndSessionStyle = EndSessionStyle.Cognito;
        var cognito = await harness.DiscoveryAsync();
        Assert.False(cognito.TryGetProperty("end_session_endpoint", out _));
    }

    [Fact]
    public async Task Discovery_omits_the_revocation_endpoint_when_the_switch_is_off()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.AdvertiseRevocation = false;

        var document = await harness.DiscoveryAsync();

        Assert.False(document.TryGetProperty("revocation_endpoint", out _));
    }

    [Fact]
    public async Task Jwks_publishes_the_current_signing_key_and_no_private_material()
    {
        await using var harness = await IdpHarness.StartAsync();

        var keys = (await harness.JwksAsync()).GetProperty("keys");

        var key = Assert.Single(keys.EnumerateArray());
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.Equal(harness.Idp.CurrentKeyId, key.GetProperty("kid").GetString());
        Assert.NotEmpty(key.GetProperty("n").GetString()!);
        Assert.Equal("AQAB", key.GetProperty("e").GetString());
        foreach (var secret in new[] { "d", "p", "q", "dp", "dq", "qi" }) Assert.False(key.TryGetProperty(secret, out _), secret);
        Assert.Equal(1, harness.Idp.JwksFetches);
    }

    [Fact]
    public async Task An_issued_access_token_validates_against_the_published_keys()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync(resource: IdpHarness.Orders);

        var result = await harness.ValidateAsync(login.AccessToken, IdpHarness.Orders);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(harness.Idp.CurrentKeyId, TestIdentityProvider.ReadJwt(login.AccessToken).Kid);
    }

    private static string[] Strings(JsonElement document, string name)
        => [.. document.GetProperty(name).EnumerateArray().Select(v => v.GetString()!)];
}
