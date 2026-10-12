using System.Net;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>Conformance of the authorization code grant: the authorize request, PKCE, and the exchange.</summary>
public sealed class TestIdpCodeGrantTests
{
    [Fact]
    public async Task Authorize_redirects_to_the_app_with_a_code_and_the_state_it_was_given()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();

        using var response = await harness.AuthorizeAsync(IdpHarness.AuthorizeQuery(Pkce.Challenge(verifier), state: "state-xyz"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(IdpHarness.Redirect1, location.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(IdpHarness.QueryValue(location, "code"));
        Assert.Equal("state-xyz", IdpHarness.QueryValue(location, "state"));

        var authorization = Assert.Single(harness.Idp.Authorizations);
        Assert.True(authorization.Ok);
        Assert.Equal(IdpHarness.Client1, authorization.ClientId);
    }

    [Theory]
    [InlineData("client_id", "nobody", "unknown_client")]
    [InlineData("redirect_uri", "https://evil.test/signin-oidc", "unregistered_redirect_uri")]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("code_challenge_method", "plain", "pkce_required")]
    [InlineData("code_challenge", "", "pkce_required")]
    public async Task Authorize_refuses_a_request_that_breaks_one_rule(string parameter, string value, string reason)
    {
        await using var harness = await IdpHarness.StartAsync();
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()));
        query[parameter] = value;

        using var response = await harness.AuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, Assert.Single(harness.Idp.Authorizations).Outcome);
    }

    [Fact]
    public async Task A_login_hint_names_the_subject_and_the_default_subject_is_used_without_one()
    {
        await using var harness = await IdpHarness.StartAsync();

        var bob = await harness.LoginAsync(loginHint: "bob");
        var alice = await harness.LoginAsync();

        Assert.Equal("bob", IdpHarness.Claim(bob.AccessToken, "sub"));
        Assert.Equal(harness.Idp.DefaultSubject, IdpHarness.Claim(alice.AccessToken, "sub"));
    }

    [Fact]
    public async Task The_code_grant_with_a_valid_verifier_issues_all_three_tokens()
    {
        await using var harness = await IdpHarness.StartAsync();

        var login = await harness.LoginAsync();

        Assert.NotNull(login.Answer.AccessToken);
        Assert.NotNull(login.Answer.RefreshToken);
        Assert.NotNull(login.Answer.IdToken);
        Assert.Equal(300, login.Answer.ExpiresIn);
        Assert.Equal(IdpHarness.Scope, login.Answer.Scope);
        Assert.Equal("access", IdpHarness.Claim(login.AccessToken, "token_use"));
        Assert.Equal(IdpHarness.Client1, IdpHarness.Claim(login.AccessToken, "client_id"));
        Assert.Equal(IdpHarness.Scope, IdpHarness.Claim(login.AccessToken, "scope"));
        var grant = Assert.Single(harness.Idp.Grants);
        Assert.Equal("authorization_code", grant.GrantType);
        Assert.True(grant.Ok);
    }

    [Fact]
    public async Task The_id_token_is_for_the_client_and_carries_the_nonce_and_at_hash()
    {
        await using var harness = await IdpHarness.StartAsync();

        var login = await harness.LoginAsync(nonce: "nonce-7");

        var result = await harness.ValidateAsync(login.IdToken, IdpHarness.Client1);
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("nonce-7", IdpHarness.Claim(login.IdToken, "nonce"));
        Assert.Equal("id", IdpHarness.Claim(login.IdToken, "token_use"));
        Assert.NotNull(IdpHarness.Claim(login.IdToken, "at_hash"));
    }

    [Fact]
    public async Task The_code_grant_with_a_wrong_or_missing_verifier_is_refused()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();

        var wrong = await harness.CodeGrantAsync(await harness.CodeAsync(Pkce.Challenge(verifier)), Pkce.NewVerifier());
        var missing = await harness.CodeGrantAsync(await harness.CodeAsync(Pkce.Challenge(verifier)), "");

        Assert.Equal("invalid_grant", wrong.Error);
        Assert.Equal("invalid_grant", missing.Error);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.All(harness.Idp.Grants, g => Assert.False(g.Ok));
    }

    [Fact]
    public async Task A_code_is_single_use()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier));

        var first = await harness.CodeGrantAsync(code, verifier);
        var replay = await harness.CodeGrantAsync(code, verifier);

        Assert.True(first.Ok);
        Assert.Equal("invalid_grant", replay.Error);
    }

    [Fact]
    public async Task A_code_issued_to_one_client_is_refused_to_another()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier));

        var injected = await harness.CodeGrantAsync(code, verifier, IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2);

        Assert.Equal("invalid_grant", injected.Error);
    }

    [Fact]
    public async Task A_code_is_bound_to_the_redirect_uri_it_was_issued_for()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier));

        var answer = await harness.CodeGrantAsync(code, verifier, redirectUri: "https://app1.spa-poc.test:8443/elsewhere");

        Assert.Equal("invalid_grant", answer.Error);
    }

    [Theory]
    [InlineData(AuthMode.Basic)]
    [InlineData(AuthMode.Post)]
    public async Task Both_client_authentication_methods_are_accepted(AuthMode auth)
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier));

        var answer = await harness.CodeGrantAsync(code, verifier, auth: auth);

        Assert.True(answer.Ok, answer.Error);
        var expected = auth == AuthMode.Basic ? ClientAuth.Basic : ClientAuth.Post;
        Assert.Equal(expected, Assert.Single(harness.Idp.Grants).Auth);
    }

    [Theory]
    [InlineData("wrong-secret", AuthMode.Basic)]
    [InlineData("wrong-secret", AuthMode.Post)]
    [InlineData(null, AuthMode.None)]
    public async Task A_client_that_cannot_authenticate_gets_invalid_client(string? secret, AuthMode auth)
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier));

        var answer = await harness.CodeGrantAsync(code, verifier, secret: secret ?? "", auth: auth);

        Assert.Equal(HttpStatusCode.Unauthorized, answer.Status);
        Assert.Equal("invalid_client", answer.Error);
    }

    [Fact]
    public async Task An_unsupported_grant_type_is_refused()
    {
        await using var harness = await IdpHarness.StartAsync();

        var answer = await harness.PostTokenAsync(new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
            IdpHarness.Client1, IdpHarness.Secret1, AuthMode.Post);

        Assert.Equal("unsupported_grant_type", answer.Error);
    }

    [Fact]
    public async Task Resource_is_mapped_to_aud_and_an_app_without_one_gets_no_audience()
    {
        await using var harness = await IdpHarness.StartAsync();

        var bound = await harness.LoginAsync(resource: IdpHarness.Orders);
        var unbound = await harness.LoginAsync(IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2);

        Assert.Equal(IdpHarness.Orders, Assert.Single(IdpHarness.Audiences(bound.AccessToken)));
        Assert.Empty(IdpHarness.Audiences(unbound.AccessToken));
        Assert.True(harness.Idp.Authorizations[0].Resource == IdpHarness.Orders);
    }

    [Fact]
    public async Task The_resource_named_at_authorize_binds_the_audience_without_the_token_request_repeating_it()
    {
        await using var harness = await IdpHarness.StartAsync();
        var verifier = Pkce.NewVerifier();
        var code = await harness.CodeAsync(Pkce.Challenge(verifier), resource: IdpHarness.Orders);

        var answer = await harness.CodeGrantAsync(code, verifier);

        Assert.Equal(IdpHarness.Orders, Assert.Single(IdpHarness.Audiences(answer.AccessToken!)));
        Assert.False(Assert.Single(harness.Idp.Grants).SentResource);
    }
}
