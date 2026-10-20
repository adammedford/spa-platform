using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Platform.Testing;
using TestIdp;

namespace Gateway.IntegrationTests.Functional;

/// <summary>
/// F-03 and F-10 (docs/plan/04-tests.md): an API call on a token that is past its expiry, alone and
/// fifty at once, through the composed gateway with a session required. Task 6.2. Beside them, the
/// same call when the provider fails the refresh: evidence T-A9, which change request 4.2 made the
/// 503 <c>idp_unavailable</c> row of contracts/http-contract.md -> Errors.
///
/// <para>Expiry is made at the provider rather than by moving a clock: the provider issues the token
/// already expired (<see cref="TestIdpFaults.AccessTokenLifetime"/>), because the token library and the
/// mock API both read the system clock (evidence T-B10). Then the provider goes back to issuing tokens
/// the mock API accepts, so the call after the refresh can succeed.</para>
/// </summary>
public sealed class RefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A token the gateway still has to refresh (it refreshes inside a minute of expiry) and the mock API accepts.</summary>
    private static readonly TimeSpan DueButValid = TimeSpan.FromSeconds(30);

    /// <summary>The echo upstream's API, which records every request that reaches it, so a call forwarded without a token would show.</summary>
    private const string EchoPath = "/api/echo/items";

    [Fact]
    public async Task F03_a_call_after_expiry_succeeds_after_one_refresh_and_the_stored_refresh_token_changes()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
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
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
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
        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
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

    /// <summary>
    /// T-A9: an outage is not a refusal. A provider that answers the refresh grant with a 5xx fails the
    /// call and leaves the session, and once the provider recovers the next call refreshes and succeeds
    /// (plan row 3.8, "any other failure leaves the session and fails the call"). 500 as well as 503,
    /// because the gateway's 503 is its own answer, not the provider's status passed on.
    /// </summary>
    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    public async Task A_provider_outage_on_refresh_is_503_idp_unavailable_and_the_session_survives_to_the_call_after_recovery(int outage)
    {
        // The case contracts/http-contract.md -> Errors names, with the status and the code its row gives.
        Contract.ErrorRow unavailable = Contract.Error("idp_unavailable");

        await using GatewayHarness harness = await GatewayHarness.StartAsync(ct: Ct);
        using TestBrowser browser = harness.CreateBrowser();

        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        TokenGrant login = Assert.Single(harness.Idp.Grants, grant => grant.GrantType == "authorization_code");
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);

        harness.Idp.Faults.RefreshOutageStatus = outage;
        BrowserResponse failed = await browser.GetAsync(EchoPath, ct: Ct);

        // The contract's answer whichever 5xx the provider gave, and no redirect, which /api never
        // answers with. The provider was asked once and failed; nothing reached the upstream, where a
        // call with no token to attach would otherwise have gone on bare.
        Assert.Equal((HttpStatusCode)unavailable.Status, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Header("Content-Type"));
        Assert.Equal(unavailable.Code, CodeOf(failed));
        Assert.Null(failed.Header("Location"));
        Assert.Equal("http_" + outage, Assert.Single(harness.Idp.RefreshGrants).Outcome);
        Assert.Empty(harness.Echo.Requests);

        // Not a rejection (change request 4.2 -> Decision), so the request line carries no rule id. The
        // line is written after the response has gone (contracts/telemetry.md), so it is waited for.
        await CapturedTelemetry.UntilAsync(
            () => FailedCallLine(harness, unavailable.Status) is not null, "the failed call's request line", Ct);
        Assert.Equal(JsonValueKind.Null, FailedCallLine(harness, unavailable.Status)!.Value.GetProperty("rule").ValueKind);

        // The session is kept: the answer clears no cookie, nothing was revoked, and the session answers.
        Assert.Empty(failed.SetCookies);
        Assert.Empty(harness.Idp.Revocations);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/bff/session", ct: Ct)).StatusCode);

        // The provider recovers, and the next call succeeds on the same session: no second login, a
        // refresh that presents the refresh token the login stored (the outage rotated nothing), and the
        // token that refresh issued is the one the upstream received. Compared as booleans and named by
        // label, so a failure prints no token.
        harness.Idp.Faults.RefreshOutageStatus = null;
        BrowserResponse recovered = await browser.GetAsync(EchoPath, ct: Ct);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Single(harness.Idp.Authorizations);
        Assert.Equal(["http_" + outage, "ok"], harness.Idp.RefreshGrants.Select(grant => grant.Outcome));
        TokenGrant refresh = harness.Idp.RefreshGrants[1];
        Assert.True(string.Equals(login.RefreshTokenIssued, refresh.RefreshTokenUsed, StringComparison.Ordinal),
            $"the refresh after recovery presented {TestIdentityProvider.Label(refresh.RefreshTokenUsed)}, not "
            + $"{TestIdentityProvider.Label(login.RefreshTokenIssued)}, which the login stored.");
        EchoedRequest forwarded = Assert.Single(harness.Echo.Requests);
        Assert.True(forwarded.Headers.TryGetValue("Authorization", out ImmutableArray<string> sent)
            && sent.SequenceEqual(["Bearer " + refresh.AccessToken]),
            $"the call after recovery did not carry {TestIdentityProvider.Label(refresh.AccessToken)}, "
            + "the token the recovering refresh issued.");
    }

    private static string? ClientOf(BrowserResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("clientId").GetString();
    }

    /// <summary>The <c>code</c> of a problem body (contracts/http-contract.md -> Errors).</summary>
    private static string? CodeOf(BrowserResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("code").GetString();
    }

    /// <summary>The request line of the API call answered with <paramref name="status"/>, or null until the telemetry stage has written it.</summary>
    private static JsonElement? FailedCallLine(GatewayHarness harness, int status) =>
        CapturedTelemetry.RequestLines(harness)
            .Where(line => line.GetProperty("route").GetString() == "api" && line.GetProperty("status").GetInt32() == status)
            .Select(line => (JsonElement?)line)
            .SingleOrDefault();
}
