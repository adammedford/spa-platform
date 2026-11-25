using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>A shell refresh on a shell with fragments, composed (#1007; #961, workstream 3's S2 regression).</summary>
public sealed class RefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// F-32: after a shell refresh rewrites the shell's tokens and the ticket round-trips the store,
    /// every fragment's set is still present and each fragment's API still receives its own token, with
    /// no fragment refresh.
    /// </summary>
    [Fact]
    public async Task G9_a_shell_refresh_keeps_every_fragment_s_token_set()
    {
        // Mutation that turns it red: a refresh that stores the shell's new tokens by replacing the
        // ticket's token items wholesale (StoreTokens over every .Token. item) drops the fragments' sets,
        // so the record's fragment tokens are null and each fragment call answers 401
        // fragment_login_required.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.Shell(2) with { KeyRing = KeyRingSharing.OneParameterStore }, Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();

        // Only the shell's set is due: its code exchange is answered with an expired access token, and
        // every fragment hop after it with a fresh one.
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        BrowserResponse start = await Steps.ToAsync(browser, shell.Url("/bff/login?returnUrl=%2F"), Ct);
        BrowserResponse beforeCallback = await Steps.UntilAsync(browser, start, url => url.AbsolutePath == "/signin-oidc", Ct);
        BrowserResponse callback = await Steps.ToAsync(browser, Steps.Next(beforeCallback)!, Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        BrowserResponse landed = await browser.NavigateAsync(Steps.Next(callback)!, ct: Ct);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);

        string key = Assert.Single(harness.SessionStore!.Created);
        SessionRecord signedIn = (await harness.SessionStore.GetAsync(key, Ct))!;
        string?[] fragmentTokens = [.. shell.Fragments.SelectMany(f => new[]
        {
            Records.FragmentToken(signedIn, f.FragmentId, "access_token"),
            Records.FragmentToken(signedIn, f.FragmentId, "refresh_token"),
        })];
        Assert.All(fragmentTokens, Assert.NotNull);

        // The shell's API call refreshes the shell's set and writes the record.
        Assert.Equal(shell.ClientId, await Fragments.ClientIdAtEchoAsync(harness, browser, shell, fragmentId: null, Ct));
        TokenGrantAssert.OnlyShell(harness, shell);
        SessionRecord refreshed = (await harness.SessionStore.GetAsync(key, Ct))!;
        Assert.True(refreshed.Version > signedIn.Version);
        Assert.NotEqual(Records.ShellToken(signedIn, "access_token"), Records.ShellToken(refreshed, "access_token"));

        // Every fragment's set is as it was, byte for byte, after the round trip through the store.
        string?[] after = [.. shell.Fragments.SelectMany(f => new[]
        {
            Records.FragmentToken(refreshed, f.FragmentId, "access_token"),
            Records.FragmentToken(refreshed, f.FragmentId, "refresh_token"),
        })];
        Assert.Equal(fragmentTokens, after);

        // And each fragment's API receives its own token still, with no fragment refresh.
        foreach (HarnessFragment fragment in shell.Fragments)
        {
            Assert.Equal(fragment.ClientId, await Fragments.ClientIdAtEchoAsync(harness, browser, shell, fragment.FragmentId, Ct));
        }

        TokenGrantAssert.OnlyShell(harness, shell);
    }
}

/// <summary>What the provider's refresh grants say about who refreshed.</summary>
internal static class TokenGrantAssert
{
    /// <summary>Exactly one refresh grant was made, by the shell's client, and it succeeded.</summary>
    public static void OnlyShell(GatewayHarness harness, HarnessApp shell)
    {
        TestIdp.TokenGrant grant = Assert.Single(harness.Idp.RefreshGrants);
        Assert.Equal(shell.ClientId, grant.ClientId);
        Assert.True(grant.Ok, grant.ToString());
    }
}
