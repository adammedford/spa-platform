using System.Net;
using Gateway.TestHarness;
using TestIdp;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The login rows of plan row 6.3: N-03, N-04, N-05, N-15 and N-18 (docs/plan/04-tests.md).
///
/// <list type="bullet">
/// <item>N-03: "Replay app 1's callback, with its code, state and login cookies, on app 2 -> Rejected.
/// No session created."</item>
/// <item>N-04: "Inject a code issued to client 1 into app 2's own login -> Rejected by the provider at
/// code exchange."</item>
/// <item>N-05: "Send a callback with no correlation cookie, no nonce cookie, or tampered state ->
/// Rejected."</item>
/// <item>N-15: "<c>returnUrl</c> of <c>//evil.test</c>, <c>https://evil.test</c> and a backslash
/// variant -> Redirects to <c>/</c>."</item>
/// <item>N-18: "Plant a session cookie, then complete a login -> The login creates a new session key.
/// The planted key never carries the new identity."</item>
/// </list>
///
/// <para>A login is stopped at the callback with <see cref="SignedIn.UpToCallbackAsync"/>, so the test
/// holds the code, the state and the login-state cookies a real browser would carry, and then sends the
/// callback the row describes. "Rejected" is read three ways: the callback's own answer (task 3.5's
/// remote-failure page, 400), no session cookie in the browser, and <c>/bff/session</c> answering 401.
/// Each row also completes an unaltered login on the same cell, so a gateway whose callbacks always
/// failed could not pass.</para>
/// </summary>
public sealed class LoginTests
{
    [Fact]
    public async Task N03_app1s_callback_replayed_on_app2_with_its_login_cookies_is_rejected_and_creates_no_session()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app1 = harness.App("app1"), app2 = harness.App("app2");
        using TestBrowser browser = harness.CreateBrowser();
        Uri callback = await SignedIn.UpToCallbackAsync(harness, browser, app1);
        string loginCookies = SignedIn.CookieHeader(browser, app1);
        Assert.Contains(Prefix(Contract.PlatformCookie("corr.*")), loginCookies, StringComparison.Ordinal);

        Uri replay = new(app2.Url("/signin-oidc"), callback.Query);
        BrowserResponse rejected = await browser.ClickAsync(
            replay.ToString(), SignedIn.Manual with { Headers = RequestOptions.With(("Cookie", loginCookies)).Headers }, Suite.Ct);

        await AssertRejectedAsync(harness, browser, app2, rejected);

        // The code was never sent to the provider by either client: nothing was exchanged.
        Assert.DoesNotContain(harness.Idp.Grants, grant => grant.GrantType == "authorization_code");

        // Control: the same callback on its own app, with the same cookies, is a login.
        BrowserResponse landed = await browser.ClickAsync(callback.ToString(), ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.True(SignedIn.HoldsSessionCookie(browser, app1));
    }

    [Fact]
    public async Task N04_a_code_issued_to_client1_injected_into_app2s_own_login_is_refused_by_the_provider()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app1 = harness.App("app1"), app2 = harness.App("app2");

        // The attacker's code, issued to app 1's client and never redeemed.
        using TestBrowser attacker = harness.CreateBrowser();
        string stolenCode = SignedIn.Query(await SignedIn.UpToCallbackAsync(harness, attacker, app1), "code");

        // The victim's own login to app 2, with its own state and cookies, carrying the attacker's code.
        using TestBrowser victim = harness.CreateBrowser();
        Uri callback = await SignedIn.UpToCallbackAsync(harness, victim, app2);
        BrowserResponse rejected = await victim.ClickAsync(
            SignedIn.WithQuery(callback, "code", stolenCode).ToString(), SignedIn.Manual, Suite.Ct);

        await AssertRejectedAsync(harness, victim, app2, rejected);

        // Rejected by the provider at code exchange: app 2's client presented it and was refused.
        TokenGrant exchange = Assert.Single(harness.Idp.Grants, grant => grant.GrantType == "authorization_code");
        Assert.Equal(app2.ClientId, exchange.ClientId);
        Assert.Equal("invalid_grant", exchange.Outcome);
        Assert.Null(exchange.AccessToken);
    }

    public static TheoryData<string> Tamperings => new() { "no correlation cookie", "no nonce cookie", "tampered state" };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task N05_a_callback_without_its_correlation_or_nonce_cookie_or_with_tampered_state_is_rejected(string tampering)
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        Uri callback = await SignedIn.UpToCallbackAsync(harness, browser, app);

        string correlation = Prefix(Contract.PlatformCookie("corr.*"));
        string nonce = Prefix(Contract.PlatformCookie("nonce.*"));
        string state = SignedIn.Query(callback, "state");
        (Uri target, string cookies) = tampering switch
        {
            "no correlation cookie" => (callback, SignedIn.CookieHeader(browser, app, name => name.StartsWith(correlation, StringComparison.Ordinal))),
            "no nonce cookie" => (callback, SignedIn.CookieHeader(browser, app, name => name.StartsWith(nonce, StringComparison.Ordinal))),
            _ => (SignedIn.WithQuery(callback, "state", Flip(state)), SignedIn.CookieHeader(browser, app)),
        };

        // Each variant takes away exactly one thing, so the header still carries the other login cookie.
        Assert.Contains(tampering == "no correlation cookie" ? nonce : correlation, cookies, StringComparison.Ordinal);

        BrowserResponse rejected = await browser.ClickAsync(
            target.ToString(), SignedIn.Manual with { Headers = RequestOptions.With(("Cookie", cookies)).Headers }, Suite.Ct);

        await AssertRejectedAsync(harness, browser, app, rejected);

        // Control: a whole login on the same cell is a session. Not a replay of this callback: without
        // the nonce cookie the handler has already redeemed the code before it refuses the ID token
        // (the nonce is checked against the token, after the exchange), and a code is single use.
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.True(SignedIn.HoldsSessionCookie(browser, app));
    }

    public static TheoryData<string> ForeignReturnUrls => new() { "//evil.test", "https://evil.test", "/\\evil.test", "\\\\evil.test" };

    [Theory]
    [MemberData(nameof(ForeignReturnUrls))]
    public async Task N15_a_return_url_that_leaves_the_app_lands_on_the_root(string returnUrl)
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse landed = await browser.NavigateAsync(
            app.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)), ct: Suite.Ct);

        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.Equal(app.Url("/"), landed.Url);
        Assert.Contains(landed.RedirectedFrom, hop => hop.AbsolutePath == "/signin-oidc");
        Assert.DoesNotContain(browser.Exchanges, exchange => exchange.Url.Host.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task N15_control_a_local_return_url_is_kept()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();

        BrowserResponse landed = await browser.NavigateAsync(
            app.Url("/bff/login?returnUrl=" + Uri.EscapeDataString("/orders/42")), ct: Suite.Ct);

        Assert.Equal(app.Url("/orders/42"), landed.Url);
    }

    /// <summary>
    /// When the cookie is planted: before the victim's login starts, which <c>/bff/login</c> answers by
    /// discarding the session it is sent; or after it started and before the callback, which only the
    /// callback's own blindness to the session cookie answers (task 3.3, R-D6). Both, because a
    /// gateway that held only one of the two defences would pass the other variant.
    /// </summary>
    public static TheoryData<string> PlantedWhen => new() { "before the login", "between login and callback" };

    [Theory]
    [MemberData(nameof(PlantedWhen))]
    public async Task N18_a_planted_session_cookie_never_carries_the_identity_of_the_login_that_follows(string when)
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");

        // The attacker's own session, under the attacker's identity.
        using TestBrowser attacker = harness.CreateBrowser();
        harness.Idp.DefaultSubject = "mallory";
        await harness.SignInAsync(attacker, "app1", Suite.Ct);
        string planted = SignedIn.SessionCookie(attacker, app);
        string plantedKey = SignedIn.KeyOf(harness, "app1", planted);

        // Planted in the victim's jar, as a sibling host could if the prefix were not enforced (R-D3:
        // the test browser enforces none), and the victim signs in as themself.
        using TestBrowser victim = harness.CreateBrowser();
        harness.Idp.DefaultSubject = "alice";
        void Plant()
        {
            victim.Cookies.SetCookies(app.Origin, $"{SignedIn.SessionCookieName}={planted}; Path=/; Secure; HttpOnly");
            Assert.Equal(planted, SignedIn.SessionCookie(victim, app));
        }

        if (when == "before the login")
        {
            Plant();
            await harness.SignInAsync(victim, "app1", Suite.Ct);
        }
        else
        {
            Uri callback = await SignedIn.UpToCallbackAsync(harness, victim, app);
            Plant();
            BrowserResponse landed = await victim.ClickAsync(callback.ToString(), ct: Suite.Ct);
            Assert.Equal(HttpStatusCode.OK, landed.StatusCode);

            // The callback really carried the planted cookie, so the new key is the gateway's doing.
            string? sent = victim.Exchanges.Last(exchange => exchange.Url.AbsolutePath == "/signin-oidc").Sent("Cookie");
            Assert.Contains(SignedIn.SessionCookieName + "=" + planted, sent, StringComparison.Ordinal);
        }

        // A new key for the new login.
        string issued = SignedIn.SessionCookie(victim, app);
        string issuedKey = SignedIn.KeyOf(harness, "app1", issued);
        Assert.NotEqual(plantedKey, issuedKey);
        Assert.Equal("alice", (await SignedIn.Store(harness).GetAsync(issuedKey, Suite.Ct))?.Subject);
        BrowserResponse victimSession = await SignedIn.SessionAsync(victim);
        Assert.Equal("alice", SignedIn.Property(victimSession, "sub"));

        // The planted key never carries alice: it is gone, or it is still mallory's.
        Assert.NotEqual("alice", (await SignedIn.Store(harness).GetAsync(plantedKey, Suite.Ct))?.Subject);
        BrowserResponse attackerSession = await SignedIn.SessionAsync(attacker);
        Assert.True(
            (int)attackerSession.StatusCode == Contract.Error("unauthenticated").Status || SignedIn.Property(attackerSession, "sub") == "mallory",
            $"the planted cookie answered {attackerSession}");
        Assert.DoesNotContain("alice", attackerSession.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three readings of "rejected, no session created": the callback's own refusal, no session
    /// cookie set, and <c>/bff/session</c> answering 401 for the browser.
    /// </summary>
    private static async Task AssertRejectedAsync(GatewayHarness harness, TestBrowser browser, HarnessApp app, BrowserResponse rejected)
    {
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); // Not an Errors row: the callback's failure page (Errors prose), not a problem body.
        Assert.DoesNotContain(rejected.SetCookies, line => line.StartsWith(SignedIn.SessionCookieName + "=", StringComparison.Ordinal));
        Assert.False(SignedIn.HoldsSessionCookie(browser, app));

        using TestBrowser probe = harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(probe, app);
        string cookies = SignedIn.CookieHeader(browser, app);
        BrowserResponse session = await SignedIn.SessionAsync(probe, cookies.Length > 0 ? cookies : null);
        Assert.Equal((HttpStatusCode)Contract.Error("unauthenticated").Status, session.StatusCode);
    }

    /// <summary>The part of a wildcard cookie name before its star: <c>__Host-spa-corr.</c>.</summary>
    private static string Prefix(string wildcard) => wildcard.TrimEnd('*');

    /// <summary>The state with one character changed, so it no longer decrypts.</summary>
    private static string Flip(string state)
    {
        int at = state.Length / 2;
        return state[..at] + (state[at] == 'A' ? 'B' : 'A') + state[(at + 1)..];
    }
}
