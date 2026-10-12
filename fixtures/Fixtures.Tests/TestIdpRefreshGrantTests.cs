using Microsoft.Extensions.Time.Testing;

namespace Fixtures.Tests;

/// <summary>
/// Conformance of the refresh grant: rotation, the grace window F-10 runs against, and the bounds on who
/// may redeem a refresh token.
/// </summary>
public sealed class TestIdpRefreshGrantTests
{
    [Fact]
    public async Task The_refresh_grant_rotates_the_refresh_token_and_issues_a_new_access_token()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.NotNull(refreshed.RefreshToken);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.NotEqual(login.AccessToken, refreshed.AccessToken);
        Assert.NotNull(refreshed.IdToken);
        Assert.True(harness.Idp.IsRotatedOut(login.RefreshToken));
        Assert.True(harness.Idp.IsRedeemable(refreshed.RefreshToken));
        var grant = Assert.Single(harness.Idp.RefreshGrants);
        Assert.Equal(login.RefreshToken, grant.RefreshTokenUsed);
        Assert.False(grant.ReplayedInsideGrace);
    }

    [Fact]
    public async Task The_audience_survives_a_refresh_that_names_no_resource()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync(resource: IdpHarness.Orders);

        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        Assert.Equal(IdpHarness.Orders, Assert.Single(IdpHarness.Audiences(refreshed.AccessToken!)));
        Assert.False(Assert.Single(harness.Idp.RefreshGrants).SentResource);
    }

    [Fact]
    public async Task A_refresh_that_names_a_resource_rebinds_the_audience()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync(resource: IdpHarness.Orders);

        var refreshed = await harness.RefreshAsync(login.RefreshToken, resource: "https://reports.spa-poc.test");

        Assert.Equal("https://reports.spa-poc.test", Assert.Single(IdpHarness.Audiences(refreshed.AccessToken!)));
        Assert.True(Assert.Single(harness.Idp.RefreshGrants).SentResource);
    }

    [Fact]
    public async Task A_refresh_token_belongs_to_the_client_it_was_issued_to()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        var stolen = await harness.RefreshAsync(login.RefreshToken, IdpHarness.Client2, IdpHarness.Secret2);

        Assert.Equal("invalid_grant", stolen.Error);
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused()
    {
        await using var harness = await IdpHarness.StartAsync();

        var answer = await harness.RefreshAsync("rt~999~never-issued");

        Assert.Equal("invalid_grant", answer.Error);
    }

    [Fact]
    public async Task A_rotated_out_refresh_token_replayed_inside_the_grace_window_is_granted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await IdpHarness.StartAsync(clock);
        var login = await harness.LoginAsync();
        var first = await harness.RefreshAsync(login.RefreshToken);

        clock.Advance(TimeSpan.FromSeconds(9));
        var late = await harness.RefreshAsync(login.RefreshToken);

        Assert.True(late.Ok, late.Error);
        Assert.True(harness.Idp.RefreshGrants[1].ReplayedInsideGrace);

        // The late caller gets a pair of its own, and the successor the first call stored still works.
        Assert.NotEqual(first.RefreshToken, late.RefreshToken);
        Assert.True(harness.Idp.IsRedeemable(first.RefreshToken));
        Assert.True(harness.Idp.IsRedeemable(late.RefreshToken));
    }

    [Fact]
    public async Task A_rotated_out_refresh_token_replayed_after_the_grace_window_is_refused_and_the_current_one_still_works()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await IdpHarness.StartAsync(clock);
        var login = await harness.LoginAsync();
        var first = await harness.RefreshAsync(login.RefreshToken);

        clock.Advance(TimeSpan.FromSeconds(11));
        var late = await harness.RefreshAsync(login.RefreshToken);

        Assert.Equal("invalid_grant", late.Error);
        Assert.False(harness.Idp.IsRedeemable(login.RefreshToken));

        // The refusal left the session's own token alone.
        var again = await harness.RefreshAsync(first.RefreshToken!);
        Assert.True(again.Ok, again.Error);
    }

    [Fact]
    public async Task The_grace_window_is_ten_seconds_by_default_and_a_test_can_close_it()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await IdpHarness.StartAsync(clock);
        Assert.Equal(TimeSpan.FromSeconds(10), harness.Idp.Faults.RefreshRotationGrace);
        harness.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        var login = await harness.LoginAsync();
        await harness.RefreshAsync(login.RefreshToken);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var late = await harness.RefreshAsync(login.RefreshToken);

        Assert.Equal("invalid_grant", late.Error);
    }

    [Fact]
    public async Task A_provider_that_does_not_rotate_hands_the_same_refresh_token_back()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.RotateRefreshTokens = false;
        var login = await harness.LoginAsync();

        var refreshed = await harness.RefreshAsync(login.RefreshToken);

        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Equal(login.RefreshToken, refreshed.RefreshToken);
        Assert.False(harness.Idp.IsRotatedOut(login.RefreshToken));
        Assert.True(harness.Idp.IsRedeemable(login.RefreshToken));
    }

    [Fact]
    public async Task A_refresh_can_be_rotated_many_times_over()
    {
        await using var harness = await IdpHarness.StartAsync();
        var token = (await harness.LoginAsync()).RefreshToken;

        for (var i = 0; i < 5; i++)
        {
            var answer = await harness.RefreshAsync(token);
            Assert.True(answer.Ok, answer.Error);
            token = answer.RefreshToken!;
        }

        Assert.Equal(5, harness.Idp.RefreshGrants.Count);
        Assert.All(harness.Idp.RefreshGrants, g => Assert.True(g.Ok));
        Assert.True(harness.Idp.IsRedeemable(token));
    }

    [Fact]
    public async Task Fifty_parallel_refreshes_inside_the_grace_window_all_succeed()
    {
        // F-10's shape at the provider: every caller presents the same token while the window is open.
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => harness.RefreshAsync(login.RefreshToken)));

        Assert.All(answers, a => Assert.True(a.Ok, a.Error));
        Assert.Equal(50, harness.Idp.RefreshGrants.Count);
        Assert.Equal(49, harness.Idp.RefreshGrants.Count(g => g.ReplayedInsideGrace));
    }
}
