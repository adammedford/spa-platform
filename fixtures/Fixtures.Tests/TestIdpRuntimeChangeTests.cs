using TestIdp;

namespace Fixtures.Tests;

/// <summary>
/// Changes a test makes while the provider is running: a client onboarded, a secret rotated, a client
/// removed, and the records the provider keeps for a test to assert on.
/// </summary>
public sealed class TestIdpRuntimeChangeTests
{
    private const string Client3 = "client-3";
    private const string Redirect3 = "https://app3.spa-poc.test:8443/signin-oidc";

    [Fact]
    public async Task A_client_onboarded_while_the_provider_runs_can_log_in()
    {
        // F-07 onboards app 3 with no restart.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.AddClient(Client3, "secret-3", Redirect3, "https://app3.spa-poc.test:8443/");

        var login = await harness.LoginAsync(Client3, "secret-3", Redirect3);

        Assert.Equal(Client3, IdpHarness.Claim(login.AccessToken, "client_id"));
        Assert.Equal(3, harness.Idp.Clients.Count);
    }

    [Fact]
    public async Task A_rotated_client_secret_is_the_only_one_the_provider_accepts()
    {
        // F-09 publishes a rotated secret and the next back-channel call must use it.
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        harness.Idp.SetClientSecret(IdpHarness.Client1, "secret-1-rotated");

        Assert.Equal("invalid_client", (await harness.RefreshAsync(login.RefreshToken)).Error);
        var refreshed = await harness.RefreshAsync(login.RefreshToken, secret: "secret-1-rotated");
        Assert.True(refreshed.Ok, refreshed.Error);
    }

    [Fact]
    public async Task A_removed_client_can_neither_log_in_nor_refresh()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        Assert.True(harness.Idp.RemoveClient(IdpHarness.Client1));

        Assert.Equal("invalid_client", (await harness.RefreshAsync(login.RefreshToken)).Error);
        using var authorize = await harness.AuthorizeAsync(IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier())));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, authorize.StatusCode);
    }

    [Fact]
    public async Task The_provider_keeps_every_token_it_issued_for_a_canary_search()
    {
        // F-05 and N-16 search a dump for the values this provider issued.
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        Assert.Contains(login.AccessToken, harness.Idp.IssuedAccessTokens);
        Assert.Contains(refreshed.AccessToken!, harness.Idp.IssuedAccessTokens);
        Assert.Contains(login.RefreshToken, harness.Idp.IssuedRefreshTokens);
        Assert.Contains(refreshed.RefreshToken!, harness.Idp.IssuedRefreshTokens);
        Assert.Equal(2, harness.Idp.IssuedAccessTokens.Count);
    }

    [Fact]
    public async Task A_request_record_names_tokens_by_label_and_never_by_value()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        var refreshed = await harness.RefreshAsync(login.RefreshToken);
        using var revoked = await harness.RevokeAsync(refreshed.RefreshToken!);

        var lines = string.Join('\n', harness.Idp.Grants.Select(g => g.ToString()).Concat(harness.Idp.Revocations.Select(r => r.ToString())));

        foreach (var token in harness.Idp.IssuedAccessTokens.Concat(harness.Idp.IssuedRefreshTokens))
            Assert.DoesNotContain(token, lines, StringComparison.Ordinal);
        Assert.Contains("rt#", lines, StringComparison.Ordinal);
        Assert.Contains("at#", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Labels_name_a_token_without_showing_it()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        Assert.Equal("(none)", TestIdentityProvider.Label(null));
        Assert.Equal("(unrecognised)", TestIdentityProvider.Label("not-a-token"));
        Assert.StartsWith("rt#", TestIdentityProvider.Label(login.RefreshToken), StringComparison.Ordinal);
        Assert.StartsWith("at#", TestIdentityProvider.Label(login.AccessToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resetting_the_log_clears_the_records_and_keeps_the_clients()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        await harness.DiscoveryAsync();

        harness.Idp.ResetLog();

        Assert.Empty(harness.Idp.Grants);
        Assert.Empty(harness.Idp.Authorizations);
        Assert.Equal(0, harness.Idp.DiscoveryFetches);
        Assert.Equal(2, harness.Idp.Clients.Count);
        Assert.True((await harness.RefreshAsync(login.RefreshToken)).Ok);
    }

    [Fact]
    public async Task The_token_endpoint_refuses_a_request_that_is_not_a_form()
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.Http.PostAsync(harness.Idp.TokenEndpoint, new StringContent("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
