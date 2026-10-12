using System.Net;

namespace Fixtures.Tests;

/// <summary>Conformance of revocation (RFC 7009), which F-04 asserts the provider saw.</summary>
public sealed class TestIdpRevocationTests
{
    [Fact]
    public async Task A_revoked_refresh_token_is_no_longer_redeemable()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var response = await harness.RevokeAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(harness.Idp.IsRedeemable(login.RefreshToken));
        Assert.Equal("invalid_grant", (await harness.RefreshAsync(login.RefreshToken)).Error);

        var revocation = Assert.Single(harness.Idp.Revocations);
        Assert.True(revocation.Known);
        Assert.Equal(1, revocation.Revoked);
        Assert.Equal("refresh_token", revocation.Hint);
        Assert.Equal(IdpHarness.Client1, revocation.ClientId);
    }

    [Fact]
    public async Task An_unknown_token_is_answered_with_200_and_revokes_nothing()
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.RevokeAsync("rt~999~never-issued");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revocation = Assert.Single(harness.Idp.Revocations);
        Assert.False(revocation.Known);
        Assert.Equal(0, revocation.Revoked);
    }

    [Fact]
    public async Task Revocation_needs_client_authentication()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var noClient = await harness.RevokeAsync(login.RefreshToken, auth: AuthMode.None);
        using var wrongSecret = await harness.RevokeAsync(login.RefreshToken, secret: "wrong-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, noClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Empty(harness.Idp.Revocations);
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task One_client_cannot_revoke_another_clients_refresh_token()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var response = await harness.RevokeAsync(login.RefreshToken, IdpHarness.Client2, IdpHarness.Secret2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(harness.Idp.Revocations).Known);
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task Revoking_a_rotated_out_token_leaves_its_successor_redeemable()
    {
        // What the G0 harness saw: a logout that races a refresh revokes the rotated-out token and
        // nobody revokes the successor (evidence T-B18).
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();
        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        using var response = await harness.RevokeAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(harness.Idp.IsRedeemable(refreshed.RefreshToken));
        Assert.True((await harness.RefreshAsync(refreshed.RefreshToken!)).Ok);
    }

    [Fact]
    public async Task The_cascade_switch_revokes_the_whole_family()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.RevokeCascadesToFamily = true;
        var login = await harness.LoginAsync();
        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        using var response = await harness.RevokeAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Assert.Single(harness.Idp.Revocations).Revoked);
        Assert.False(harness.Idp.IsRedeemable(refreshed.RefreshToken));
        Assert.Equal("invalid_grant", (await harness.RefreshAsync(refreshed.RefreshToken!)).Error);
    }

    [Fact]
    public async Task Revoking_every_token_of_a_client_ends_each_of_its_sessions()
    {
        await using var harness = await IdpHarness.StartAsync();
        var first = await harness.LoginAsync();
        var second = await harness.LoginAsync(loginHint: "bob");
        var other = await harness.LoginAsync(IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2);

        var revoked = harness.Idp.RevokeAllForClient(IdpHarness.Client1);

        Assert.Equal(2, revoked);
        Assert.False(harness.Idp.IsRedeemable(first.RefreshToken));
        Assert.False(harness.Idp.IsRedeemable(second.RefreshToken));
        Assert.True(harness.Idp.IsRedeemable(other.RefreshToken));
    }
}
