using System.Net;
using Gateway.SecurityTests.Negative;
using Gateway.TestHarness;

namespace Gateway.SecurityTests.Fragments;

/// <summary>
/// O11 of the fragments spec (#961), composed (#1007): one upstream serves the shell's client and a
/// fragment's, each on its own route through the gateway, and tells them apart only by
/// <c>client_id</c> (contracts/upstream-contract.md -> "Fragment routes"). The shell is
/// <c>fixtures/fragments/shell.json</c> with its <c>orders</c> fragment; both name the mock API's
/// <c>orders</c> listener, on which the harness admits both clients.
/// </summary>
public sealed class CrossClientTests
{
    /// <summary>
    /// F-38: <c>/whoami</c> on one upstream through <c>/api/x/</c> reports the shell's
    /// <c>client_id</c> and through <c>/fragments/a/api/x/</c> the fragment's. And the shell's token
    /// never reaches the fragment route.
    /// </summary>
    [Fact]
    public async Task G9_O11_whoami_through_the_shell_and_the_fragment_route_reports_each_client_id()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.Shell(1), Suite.Ct);
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = shell.Fragment("orders");
        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await FragmentLogins.LoginAsync(browser, shell)).StatusCode);

        BrowserResponse viaShell = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        BrowserResponse viaFragment = await browser.GetAsync("/fragments/orders/api/orders/whoami", ct: Suite.Ct);

        // Mutation that turns it red: a fragment route that forwards the shell's token set (the proxy's
        // token step ignoring IFragmentFeature) reports the shell's client on both.
        Assert.Equal(HttpStatusCode.OK, viaShell.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaFragment.StatusCode);
        Assert.Equal(shell.ClientId, FragmentLogins.Property(viaShell, "clientId"));
        Assert.Equal(orders.ClientId, FragmentLogins.Property(viaFragment, "clientId"));
        Assert.Equal("orders", FragmentLogins.Property(viaFragment, "api"));

        // The token itself, at the echo upstream: the fragment route's bearer is not the shell's, and
        // was issued to the fragment's client. Mutation: the same forwarding fault makes the two equal.
        string shellToken = await FragmentLogins.TokenAtEchoAsync(harness, browser, shell, fragmentId: null);
        string fragmentToken = await FragmentLogins.TokenAtEchoAsync(harness, browser, shell, "orders");
        Assert.NotEqual(shellToken, fragmentToken);
        Assert.Equal(shell.ClientId, FragmentRequests.ClaimOf(shellToken, "client_id"));
        Assert.Equal(orders.ClientId, FragmentRequests.ClaimOf(fragmentToken, "client_id"));
    }

    /// <summary>
    /// N-37: a fragment's token sent where only the shell's client is admitted, and the shell's where
    /// only the fragment's is, are each refused by the mock API with 403 <c>client_not_admitted</c>,
    /// by <c>client_id</c>, in the N-17 shape (the status and the error in the body, passed back by
    /// the gateway).
    /// </summary>
    [Fact]
    public async Task G9_O11_a_cross_client_call_is_refused_by_client_id()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.Shell(1), Suite.Ct);
        HarnessApp shell = harness.App("shell");
        HarnessFragment orders = shell.Fragment("orders");

        // The API owner's decision, per route: /echo admits the shell's client only, /admin the
        // fragment's only. /whoami keeps the listener's list, which admits both.
        harness.MockApi.AdmissionOf("orders").Admit(shell.ClientId, "/echo");
        harness.MockApi.AdmissionOf("orders").Admit(orders.ClientId, "/admin");

        using TestBrowser browser = harness.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await FragmentLogins.LoginAsync(browser, shell)).StatusCode);

        BrowserResponse shellOnEcho = await browser.GetAsync("/api/orders/echo", ct: Suite.Ct);
        BrowserResponse fragmentOnEcho = await browser.GetAsync("/fragments/orders/api/orders/echo", ct: Suite.Ct);
        BrowserResponse shellOnAdmin = await browser.GetAsync("/api/orders/admin", ct: Suite.Ct);
        BrowserResponse fragmentOnAdmin = await browser.GetAsync("/fragments/orders/api/orders/admin", ct: Suite.Ct);

        // Controls: each client's token is good on the route that admits it, so the refusals are about
        // the client. The fragment on /admin is admitted and then lacks the admin scope, which no app
        // asks for (F-08), so its refusal is a scope decision and not an admission one.
        Assert.Equal(HttpStatusCode.OK, shellOnEcho.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fragmentOnAdmin.StatusCode);
        Assert.Equal("insufficient_scope", FragmentLogins.Property(fragmentOnAdmin, "error"));

        // Mutation that turns it red: a fragment route forwarding the shell's token makes the
        // fragment's /echo call 200 (the shell's client is admitted there); a shell route forwarding a
        // fragment's token does the same to the shell's /admin call (insufficient_scope, not this).
        foreach (BrowserResponse refused in (BrowserResponse[])[fragmentOnEcho, shellOnAdmin])
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); // Not an Errors row: the mock API's own answer, passed back.
            Assert.Contains("\"error\":\"client_not_admitted\"", refused.Body, StringComparison.Ordinal);
            Assert.Equal("orders", FragmentLogins.Property(refused, "api"));
        }
    }
}
