using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-03 and F-10 (docs/plan/04-tests.md): an API call on a token that is past its expiry, alone and
/// fifty at once, through the composed gateway with a session required. Task 6.2.
///
/// <para>Expiry is made at the provider rather than by moving a clock: the provider issues the token
/// already expired (<see cref="TestIdpFaults.AccessTokenLifetime"/>), because the token library and the
/// mock API both read the system clock (evidence T-B10). Then the provider goes back to issuing tokens
/// the mock API accepts, so the call after the refresh can succeed.</para>
/// </summary>
public sealed class RefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GatewayHarnessOptions Options = new() { SessionRequired = true };

    /// <summary>A token the gateway still has to refresh (it refreshes inside a minute of expiry) and the mock API accepts.</summary>
    private static readonly TimeSpan DueButValid = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task F03_a_call_after_expiry_succeeds_after_one_refresh_and_the_stored_refresh_token_changes()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();
        HarnessApp app = harness.App("app1");

        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-1);
        await harness.SignInAsync(browser, "app1", Ct);
        TokenGrant login = Assert.Single(harness.Idp.Grants, grant => grant.GrantType == "authorization_code");

        // The first call: the token is past expiry, so it is refreshed once, and the refreshed token is
        // still due, so the next call has to refresh again and shows which refresh token is stored.
        harness.Idp.Faults.AccessTokenLifetime = DueButValid;
        BrowserResponse first = await browser.GetAsync("/api/orders/whoami", ct: Ct);
        TokenGrant refresh = Assert.Single(harness.Idp.RefreshGrants);

        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        BrowserResponse second = await browser.GetAsync("/api/orders/whoami", ct: Ct);
        BrowserResponse third = await browser.GetAsync("/api/orders/whoami", ct: Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(app.ClientId, ClientOf(first));

        // One grant for the first call, by app 1's own client, with the refresh token the login stored.
        Assert.True(refresh.Ok, refresh.ToString());
        Assert.Equal(app.ClientId, refresh.ClientId);
        Assert.Equal(login.RefreshTokenIssued, refresh.RefreshTokenUsed);
        Assert.NotEqual(login.RefreshTokenIssued, refresh.RefreshTokenIssued);

        // The stored refresh token changed: the next refresh presents the rotated one, not the login's.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, harness.Idp.RefreshGrants.Count);
        Assert.Equal(refresh.RefreshTokenIssued, harness.Idp.RefreshGrants[1].RefreshTokenUsed);

        // A token with time left is used, not refreshed.
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, harness.Idp.RefreshGrants.Count);
        Assert.All(harness.Idp.RefreshGrants, grant => Assert.False(grant.ReplayedInsideGrace, grant.ToString()));
    }

    [Fact]
    public async Task F03_control_the_call_carries_the_refreshed_token_not_the_expired_one()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        BrowserResponse call = await browser.GetAsync("/api/orders/whoami", ct: Ct);

        // Ten minutes past expiry is outside the mock API's clock skew, so the call could only succeed
        // with the token the refresh issued. Without a refresh this row would fail here.
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Single(harness.Idp.RefreshGrants);
    }

    [Fact]
    public async Task F10_fifty_parallel_calls_on_an_expired_token_all_succeed_on_one_refresh_and_the_session_survives()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(Options, Ct);
        using TestBrowser browser = harness.CreateBrowser();

        // The 10-second grace window task 1.8 gives Cognito, stated rather than left to the default.
        harness.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(10);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);

        // Slow token answers, so the fifty calls are all waiting while the refresh is in flight.
        harness.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);
        harness.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromMilliseconds(200);

        BrowserResponse[] calls = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            browser.GetAsync("/api/orders/whoami?call=" + i, ct: Ct)));
        BrowserResponse session = await browser.GetAsync("/bff/session", ct: Ct);

        Assert.All(calls, call => Assert.Equal(HttpStatusCode.OK, call.StatusCode));
        Assert.All(calls, call => Assert.Equal(harness.App("app1").ClientId, ClientOf(call)));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        // No grant refused, and one refresh, plus any late call's replay inside the window.
        Assert.All(harness.Idp.RefreshGrants, grant => Assert.True(grant.Ok, grant.ToString()));
        Assert.Single(harness.Idp.RefreshGrants, grant => !grant.ReplayedInsideGrace);
    }

    private static string? ClientOf(BrowserResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("clientId").GetString();
    }
}
