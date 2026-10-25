using System.Net;
using Gateway.TestHarness;
using Platform.Registry;
using static Gateway.IntegrationTests.Continuity.Logins;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// Rotation across two hosts (#332, F7 and F9; #345): an app rotated onto a new client while host A is
/// polled and host B is not.
/// </summary>
public sealed class RotationTests
{
    /// <summary>
    /// The rotation as the composed cell runs it: A's swap purges the app's sessions from the one store
    /// every host reads (<c>OptionsInvalidator</c>, plan row 3.6), so B, still at the old revision, refuses
    /// the old session on its next request too. That is earlier than F7's bound ("after its next
    /// successful poll"), never later.
    /// </summary>
    [Fact]
    public async Task P2_5_F7_a_rotation_polled_on_A_only_refuses_the_old_session_on_A_and_through_the_shared_store_on_B()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        GatewayHost target = a;
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Ct);
        target = b;
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/bff/session", ct: Ct)).StatusCode);

        (_, _, RefreshOutcome rotated) = await harness.RotateClientAsync("app1", Ct);
        Assert.Equal(RefreshOutcome.Swapped, rotated);
        Assert.Equal((HttpStatusCode.OK, 1L, "host-b"), await b.ReadyzAsync(Ct));

        target = a;
        BrowserResponse onA = await browser.GetAsync("/bff/session", ct: Ct);
        target = b;
        BrowserResponse onB = await browser.GetAsync("/bff/session", ct: Ct);

        // Mutation that turns it red: a purge that does not reach the store (FailingPurge set, the row
        // below) leaves B serving the old session, 200.
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, onA.StatusCode);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, onB.StatusCode);
        Assert.Equal((HttpStatusCode.OK, 1L, "host-b"), await b.ReadyzAsync(Ct));
    }

    /// <summary>
    /// The skew F7 bounds, made visible by a purge that fails: A refuses the old session on its next
    /// request through the ticket store's client binding (N-22), and B, which has not polled and still
    /// names the old client, serves it until it is polled, then refuses it.
    /// </summary>
    [Fact]
    public async Task P2_5_F7_with_the_purge_failing_A_refuses_the_old_session_at_once_and_B_serves_it_until_polled()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        GatewayHost target = a;
        using TestBrowser browser = harness.CreateBrowser(() => target);
        await harness.SignInAsync(browser, "app1", Ct);

        harness.SessionStore!.FailingPurge = true;
        await harness.RotateClientAsync("app1", Ct);

        target = a;
        BrowserResponse onA = await browser.GetAsync("/bff/session", ct: Ct);
        target = b;
        BrowserResponse onBBeforePoll = await browser.GetAsync("/bff/session", ct: Ct);
        Assert.Equal(RefreshOutcome.Swapped, await b.RefreshRegistryAsync(Ct));
        BrowserResponse onBAfterPoll = await browser.GetAsync("/bff/session", ct: Ct);

        Assert.True(harness.SessionStore.Refused >= 1, "the purge was never attempted, so the row proves nothing about it.");
        // Mutation that turns it red: removing B's poll leaves the last request 200; a harness whose purge
        // succeeds (FailingPurge left unset, the row above) makes B's first answer 401.
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, onA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onBBeforePoll.StatusCode);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, onBAfterPoll.StatusCode);
    }

    [Fact]
    public async Task P2_5_F9_a_login_started_under_the_old_client_and_called_back_after_convergence_is_refused_and_a_fresh_login_succeeds()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        HarnessApp before = harness.App("app1");

        GatewayHost target = a;
        using TestBrowser browser = harness.CreateBrowser(() => target);
        Uri callback = await UpToCallbackAsync(harness, browser, before);

        (_, HarnessApp after, _) = await harness.RotateClientAsync("app1", Ct);
        Assert.Equal(RefreshOutcome.Swapped, await b.RefreshRegistryAsync(Ct));
        target = b;
        BrowserResponse stale = await browser.NavigateAsync(callback, Manual, Ct);

        BrowserResponse fresh = await harness.SignInAsync(browser, "app1", Ct);

        // The old client's code is redeemed under the new client and the provider refuses it: the
        // callback's one failure page (contracts/http-contract.md, Errors prose), and no session.
        // Mutation that turns it red: a rotation that keeps the client id (RotateClientAsync giving
        // `after` the old ClientId, as a secret-only rotation does) lands the stale callback on the shell.
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode); // Not an Errors row: the callback's failure page.
        Assert.StartsWith("text/html", stale.Header("Content-Type"), StringComparison.Ordinal);
        Assert.DoesNotContain(stale.SetCookies, line => line.StartsWith(Contract.PlatformCookie("-session") + "=", StringComparison.Ordinal));

        // A fresh login succeeds, under the new client.
        AssertOnShell(after, fresh);
        Uri authorize = Assert.Single(fresh.RedirectedFrom, hop => hop.AbsoluteUri.StartsWith(harness.Idp.AuthorizeEndpoint, StringComparison.Ordinal));
        Assert.Contains("client_id=" + Uri.EscapeDataString(after.ClientId), authorize.Query, StringComparison.Ordinal);
        Assert.NotEqual(before.ClientId, after.ClientId);
    }
}
