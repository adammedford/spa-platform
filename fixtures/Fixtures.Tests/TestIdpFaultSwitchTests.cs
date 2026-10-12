using System.Diagnostics;
using System.Net;
using Microsoft.IdentityModel.Tokens;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>
/// Each fault switch, turned on a running provider, produces its fault. These are the behaviours a real
/// provider will not give on demand, which is why the hermetic tier has this double at all.
/// </summary>
public sealed class TestIdpFaultSwitchTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_short_access_token_lifetime_is_what_the_answer_and_the_token_say()
    {
        // Tests F-03 and F-10 shorten the lifetime here instead of moving a clock (evidence T-B10).
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromSeconds(30);

        var login = await harness.LoginAsync();

        Assert.Equal(30, login.Answer.ExpiresIn);
        var token = TestIdentityProvider.ReadJwt(login.AccessToken);
        Assert.InRange((token.ValidTo - token.ValidFrom).TotalSeconds, 30, 40);
        Assert.True((await harness.ValidateAsync(login.AccessToken)).IsValid);
    }

    [Fact]
    public async Task A_negative_lifetime_issues_an_already_expired_access_token()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-5);

        var login = await harness.LoginAsync();

        var result = await harness.ValidateAsync(login.AccessToken);
        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Fact]
    public async Task A_refused_refresh_is_invalid_grant_and_rotates_nothing()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        harness.Idp.Faults.RefuseRefresh = true;

        var refused = await harness.RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("invalid_grant", refused.Error);
        Assert.False(harness.Idp.IsRotatedOut(login.RefreshToken));

        // Switches are settable without a restart: turning it off serves the very next request.
        harness.Idp.Faults.RefuseRefresh = false;
        Assert.True((await harness.RefreshAsync(login.RefreshToken)).Ok);
    }

    [Fact]
    public async Task An_outage_answers_with_its_status_and_rotates_nothing()
    {
        // A client cannot tell this from a refusal by the result alone (evidence T-A8, T-A9, T-A15).
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        harness.Idp.Faults.RefreshOutageStatus = 503;

        var answer = await harness.RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.Status);
        Assert.Null(answer.Error);
        Assert.False(harness.Idp.IsRotatedOut(login.RefreshToken));
        Assert.Equal("http_503", Assert.Single(harness.Idp.RefreshGrants).Outcome);
    }

    [Fact]
    public async Task A_delay_on_the_token_endpoint_holds_every_grant_back()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.TokenDelay = Delay;

        var started = Stopwatch.StartNew();
        var login = await harness.LoginAsync();
        var elapsed = started.Elapsed;

        Assert.True(login.Answer.Ok);
        Assert.True(elapsed >= Delay - Tolerance, $"took {elapsed}");
    }

    [Fact]
    public async Task A_delay_between_rotating_and_answering_holds_the_answer_back()
    {
        // The gap F-10 lives in: the refresh token is already rotated and the caller does not know yet.
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        harness.Idp.Faults.RefreshAnswerDelay = Delay;

        var started = Stopwatch.StartNew();
        var answer = await harness.RefreshAsync(login.RefreshToken);
        var elapsed = started.Elapsed;

        Assert.True(answer.Ok, answer.Error);
        Assert.True(elapsed >= Delay - Tolerance, $"took {elapsed}");
        Assert.True(harness.Idp.IsRotatedOut(login.RefreshToken));
    }

    [Fact]
    public async Task A_delay_on_discovery_holds_the_document_back()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.DiscoveryDelay = Delay;

        var started = Stopwatch.StartNew();
        await harness.DiscoveryAsync();
        var elapsed = started.Elapsed;

        Assert.True(elapsed >= Delay - Tolerance, $"took {elapsed}");
    }

    [Fact]
    public async Task A_rotated_signing_key_signs_the_next_token_and_the_retired_key_stays_published()
    {
        await using var harness = await IdpHarness.StartAsync();
        var before = await harness.LoginAsync();
        var firstKid = harness.Idp.CurrentKeyId;

        var newKid = harness.Idp.RotateSigningKey();
        var after = await harness.LoginAsync();

        Assert.NotEqual(firstKid, newKid);
        Assert.Equal(firstKid, TestIdentityProvider.ReadJwt(before.AccessToken).Kid);
        Assert.Equal(newKid, TestIdentityProvider.ReadJwt(after.AccessToken).Kid);

        var kids = (await harness.JwksAsync()).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        Assert.Equal(new string?[] { firstKid, newKid }, kids);
        Assert.True((await harness.ValidateAsync(before.AccessToken)).IsValid);
        Assert.True((await harness.ValidateAsync(after.AccessToken)).IsValid);
    }

    [Fact]
    public async Task A_retired_key_dropped_from_the_key_set_stops_the_tokens_it_signed_validating()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.PublishRetiredKeys = false;
        var before = await harness.LoginAsync();

        harness.Idp.RotateSigningKey();
        var after = await harness.LoginAsync();

        Assert.Single((await harness.JwksAsync()).GetProperty("keys").EnumerateArray());
        var stale = await harness.ValidateAsync(before.AccessToken);
        Assert.False(stale.IsValid);
        Assert.True((await harness.ValidateAsync(after.AccessToken)).IsValid);
    }

    [Fact]
    public async Task A_token_signed_with_the_unpublished_key_fails_signature_validation()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.SignWithUnpublishedKey = true;

        var login = await harness.LoginAsync();

        var kids = (await harness.JwksAsync()).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        Assert.DoesNotContain(TestIdentityProvider.ReadJwt(login.AccessToken).Kid, kids);
        var result = await harness.ValidateAsync(login.AccessToken);
        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenSignatureKeyNotFoundException>(result.Exception);
    }

    [Fact]
    public async Task An_issuer_override_mints_a_token_from_the_wrong_issuer()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.IssuerOverride = "https://not-this-issuer.test";

        var login = await harness.LoginAsync();

        Assert.Equal("https://not-this-issuer.test", TestIdentityProvider.ReadJwt(login.AccessToken).Issuer);
        var result = await harness.ValidateAsync(login.AccessToken);
        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenInvalidIssuerException>(result.Exception);

        // Discovery keeps naming the provider's own origin, so a client that reads it is not confused.
        Assert.Equal(harness.Idp.BaseUrl, (await harness.DiscoveryAsync()).GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task The_token_use_switch_mints_a_token_the_api_must_refuse_on_that_claim()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.AccessTokenUse = "id";

        var login = await harness.LoginAsync();

        Assert.Equal("id", IdpHarness.Claim(login.AccessToken, "token_use"));
    }

    [Fact]
    public async Task Ignoring_the_resource_mints_a_token_with_no_audience()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.IgnoreResource = true;

        var login = await harness.LoginAsync(resource: IdpHarness.Orders);

        Assert.Empty(IdpHarness.Audiences(login.AccessToken));
        Assert.Equal(IdpHarness.Orders, Assert.Single(harness.Idp.Authorizations).Resource);
    }

    [Fact]
    public async Task Padding_makes_a_refresh_token_longer_than_a_client_library_accepts()
    {
        // The token-management library refuses one over 4,096 characters (evidence T-B11).
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.RefreshTokenPadding = 5000;

        var login = await harness.LoginAsync();

        Assert.True(login.RefreshToken.Length > 4096, $"length {login.RefreshToken.Length}");
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task A_refresh_answer_can_leave_the_id_token_out()
    {
        // Which blanks a stored ID token and sends an empty id_token_hint at logout (evidence T-B13).
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.IdTokenOnRefresh = false;
        var login = await harness.LoginAsync();

        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        Assert.NotNull(login.Answer.IdToken);
        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Null(refreshed.IdToken);
    }

    [Fact]
    public async Task Revocation_can_be_made_to_fail_and_then_revokes_nothing()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        harness.Idp.Faults.RevokeFailureStatus = 500;

        using var response = await harness.RevokeAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(harness.Idp.Revocations);
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task A_delay_on_revocation_and_on_logout_holds_both_back()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        harness.Idp.Faults.RevokeDelay = Delay;
        harness.Idp.Faults.EndSessionDelay = Delay;

        var started = Stopwatch.StartNew();
        using var revoked = await harness.RevokeAsync(login.RefreshToken);
        using var loggedOut = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["logout_uri"] = IdpHarness.PostLogout1,
        });
        var elapsed = started.Elapsed;

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        Assert.True(elapsed >= Delay + Delay - Tolerance, $"took {elapsed}");
    }

    [Fact]
    public async Task A_delay_on_authorize_and_on_the_key_set_holds_both_back()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.AuthorizeDelay = Delay;
        harness.Idp.Faults.JwksDelay = Delay;

        var started = Stopwatch.StartNew();
        await harness.CodeAsync(Pkce.Challenge(Pkce.NewVerifier()));
        await harness.JwksAsync();
        var elapsed = started.Elapsed;

        Assert.True(elapsed >= Delay + Delay - Tolerance, $"took {elapsed}");
    }
}
