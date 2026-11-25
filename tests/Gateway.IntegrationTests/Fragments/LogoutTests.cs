using System.Net;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Abstractions;
using TestIdp;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// Logout on a shell with fragments, composed (#1007, part of #961 -> "Logout"): N revocations, each
/// through its own client, then one provider logout by the shell's client. Workstream 3 proved the
/// same against its doubles (FragmentLogoutTests in tests/Platform.Identity.Tests).
/// </summary>
public sealed class LogoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// F-26: one <c>POST /bff/logout</c> with N = 2 makes three revocations at TestIdp, each by the set's
    /// own client in the profile's credential style (the form body under <c>standard</c>, a Basic header
    /// under <c>cognito</c>), logs one revocation line per set with its outcome, removes the record, then
    /// sends one provider logout by the shell's client, with its ID token under <c>standard</c>.
    /// </summary>
    [Theory]
    [InlineData("standard", ClientAuth.Post)]
    [InlineData("cognito", ClientAuth.Basic)]
    public async Task G9_logout_revokes_every_token_set_by_its_own_client_then_ends_the_provider_session_once(string profile, ClientAuth style)
    {
        // Mutation that turns it red: revoking a fragment's set through the shell's scheme records the
        // shell's client for it at the provider (and the provider refuses the token as not its own), so
        // the three-client dictionary and the Known checks fail.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.Shell(2) with { Profile = profile, KeyRing = KeyRingSharing.OneParameterStore }, Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        await Fragments.LoginAsync(browser, shell, "/", Ct);
        string key = Assert.Single(harness.SessionStore!.Created);
        SessionRecord held = (await harness.SessionStore.GetAsync(key, Ct))!;

        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: Steps.Manual, ct: Ct);

        // The revocations are made before the answer; the provider logout only once the browser follows it.
        Dictionary<string, Revocation> byClient = harness.Idp.Revocations.ToDictionary(r => r.ClientId, StringComparer.Ordinal);
        Assert.Equal(3, byClient.Count);
        Assert.Equal(Records.ShellToken(held, "refresh_token"), byClient[shell.ClientId].Token);
        Assert.Equal(Records.FragmentToken(held, "orders", "refresh_token"), byClient[shell.Fragment("orders").ClientId].Token);
        Assert.Equal(Records.FragmentToken(held, "reports", "refresh_token"), byClient[shell.Fragment("reports").ClientId].Token);
        Assert.All(byClient.Values, r => Assert.True(r is { Known: true, Revoked: > 0 }, r.ToString()));
        Assert.All(byClient.Values, r => Assert.Equal(style, r.Auth));
        Assert.Empty(harness.Idp.Logouts);

        string[] lines = [.. harness.Log.Lines.Select(line => line.Text)
            .Where(text => text.StartsWith("Logout for app shell is removing session", StringComparison.Ordinal))];
        Assert.Equal(3, lines.Length);
        Assert.Single(lines, l => l.EndsWith("revoking its refresh token: revoked.", StringComparison.Ordinal));
        Assert.Single(lines, l => l.EndsWith("of fragment orders: revoked.", StringComparison.Ordinal));
        Assert.Single(lines, l => l.EndsWith("of fragment reports: revoked.", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Contains(SessionRef.Of(key), l, StringComparison.Ordinal));
        Assert.Null(await harness.SessionStore.GetAsync(key, Ct));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Uri endSession = Steps.Next(logout)!;
        if (profile == "standard")
        {
            Assert.Equal(harness.Idp.EndSessionEndpoint, endSession.GetLeftPart(UriPartial.Path));
            Assert.Equal(Records.ShellToken(held, "id_token"), QueryHelpers.ParseQuery(endSession.Query)["id_token_hint"].ToString());
        }
        else
        {
            Assert.Equal(harness.Idp.CognitoLogoutEndpoint, endSession.GetLeftPart(UriPartial.Path));
        }

        await browser.NavigateAsync(endSession, Steps.Manual, Ct);
        TestIdp.Logout ended = Assert.Single(harness.Idp.Logouts);
        Assert.True(ended.Ok, ended.Outcome);
        Assert.Equal(shell.ClientId, ended.ClientId);
    }
}
