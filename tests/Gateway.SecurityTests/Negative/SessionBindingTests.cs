using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;
using Platform.Registry;
using TestIdp;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-01, N-02, N-21 and N-22: a session cookie is good for one app, one client and one live record,
/// and for nothing else (docs/plan/04-tests.md; ADR-0004, ADR-0017).
///
/// <list type="bullet">
/// <item>N-01: "Present app 1's session cookie to app 2 -> 401 on the API, a fresh challenge on
/// navigation."</item>
/// <item>N-02: "Forge an app 2 cookie that names app 1's session key -> 401. The store is bound to app
/// and client."</item>
/// <item>N-21: "Present a cookie whose session was removed -> 401. Decryption succeeds; the store
/// lookup does not."</item>
/// <item>N-22: "Use a session after its app was removed or its client replaced -> 401."</item>
/// </list>
///
/// <para>Each row ends with a control that the same cookie, or the same key, is still good where it
/// belongs, so a gateway that refused every cookie could not pass (CODING_STANDARDS.md -> "An
/// assertion must be able to fail").</para>
/// </summary>
public sealed class SessionBindingTests
{
    [Fact]
    public async Task N01_app1s_session_cookie_is_401_on_app2s_api_and_a_fresh_challenge_on_navigation()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        HarnessApp app1 = harness.App("app1"), app2 = harness.App("app2");
        using TestBrowser victim = harness.CreateBrowser();
        await harness.SignInAsync(victim, "app1", Suite.Ct);
        string cookie = SignedIn.SessionCookieName + "=" + SignedIn.SessionCookie(victim, app1);

        // The cookie is __Host- and host-only, so no browser sends it to app 2 by itself: the attacker
        // copies it there, which is what the header override does.
        using TestBrowser attacker = harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(attacker, app2);
        BrowserResponse api = await attacker.GetAsync("/api/reports/whoami", RequestOptions.With(("Cookie", cookie)), Suite.Ct);
        BrowserResponse session = await SignedIn.SessionAsync(attacker, cookie);

        Contract.ErrorRow unauthenticated = Contract.Error("No session");
        foreach (BrowserResponse refused in (BrowserResponse[])[api, session])
        {
            Assert.Equal((HttpStatusCode)unauthenticated.Status, refused.StatusCode);
            Assert.Contains($"\"code\":\"{unauthenticated.Code}\"", refused.Body, StringComparison.Ordinal);
            Assert.Null(refused.Header("Location"));
        }

        // A navigation is challenged afresh, for app 2's own client: not served the shell on app 1's say-so.
        BrowserResponse navigation = await attacker.NavigateAsync(app2.Url("/"), SignedIn.Manual with
        {
            Headers = RequestOptions.With(("Cookie", cookie)).Headers,
        }, Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, navigation.StatusCode);
        Uri challenge = new(navigation.Header("Location")!);
        Assert.Equal(harness.Idp.AuthorizeEndpoint, challenge.GetLeftPart(UriPartial.Path));
        Assert.Equal(app2.ClientId, SignedIn.Query(challenge, "client_id"));

        // Control: the same cookie is a session on app 1.
        BrowserResponse own = await SignedIn.SessionAsync(victim);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task N02_an_app2_cookie_forged_to_name_app1s_session_key_is_401_and_app1s_session_is_untouched()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        HarnessApp app1 = harness.App("app1"), app2 = harness.App("app2");
        using TestBrowser victim = harness.CreateBrowser();
        await harness.SignInAsync(victim, "app1", Suite.Ct);
        string app1Cookie = SignedIn.SessionCookie(victim, app1);
        string app1Key = SignedIn.KeyOf(harness, "app1", app1Cookie);

        // The strongest forgery there is: app 1's own ticket, encrypted with app 2's keys, so it
        // decrypts on app 2 and names app 1's live key. Only the store's binding is left to refuse it.
        AuthenticationTicket app1Ticket = Assert.IsType<AuthenticationTicket>(SignedIn.Unprotect(harness, "app1", app1Cookie));
        string forged = SignedIn.Protect(harness, "app2",
            new AuthenticationTicket(app1Ticket.Principal, app1Ticket.Properties, SchemeNames.Cookie("app2")));
        Assert.Equal(app1Key, SignedIn.KeyOf(harness, "app2", forged));

        // Control on the forgery's other half: app 1's cookie does not decrypt for app 2, so without the
        // forgery the row would be about encryption and not about the store.
        Assert.Null(SignedIn.Unprotect(harness, "app2", app1Cookie));

        using TestBrowser attacker = harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(attacker, app2);
        string cookie = SignedIn.SessionCookieName + "=" + forged;
        BrowserResponse session = await SignedIn.SessionAsync(attacker, cookie);
        BrowserResponse api = await attacker.GetAsync("/api/reports/whoami", RequestOptions.With(("Cookie", cookie)), Suite.Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.DoesNotContain(harness.Idp.DefaultSubject, session.Body, StringComparison.Ordinal);

        // App 2 refused the record without removing it: it is still app 1's, and still good on app 1.
        SessionRecord record = Assert.IsType<SessionRecord>(await SignedIn.Store(harness).GetAsync(app1Key, Suite.Ct));
        Assert.Equal("app1", record.AppId);
        Assert.Equal(app1.ClientId, record.ClientId);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(victim)).StatusCode);
    }

    [Fact]
    public async Task N21_a_cookie_whose_session_was_removed_decrypts_and_is_401()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);
        string cookie = SignedIn.SessionCookie(browser, app);
        string key = SignedIn.KeyOf(harness, "app1", cookie);

        await SignedIn.Store(harness).RemoveAsync(key, Suite.Ct);

        // Decryption succeeds: the same cookie still opens with the app's format and names the same key.
        Assert.Equal(key, SignedIn.KeyOf(harness, "app1", cookie));
        Assert.Equal(cookie, SignedIn.SessionCookie(browser, app));

        // The store lookup does not.
        BrowserResponse session = await SignedIn.SessionAsync(browser);
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Null(api.Header("Location"));
    }

    [Fact]
    public async Task N22_a_session_is_401_after_its_app_was_removed_and_added_back()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);
        JsonNode entry = Entry(harness.RegistryDocument, "app1").DeepClone();

        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(
            document => document["apps"]!.AsArray().Remove(Entry(document, "app1")), Suite.Ct));

        // Back under the same host, client and release, so the only thing that changed is that the app
        // left the registry for one revision: a session that survived would be found by the same cookie.
        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(
            document => document["apps"]!.AsArray().Add(entry.DeepClone()), Suite.Ct));

        BrowserResponse session = await SignedIn.SessionAsync(browser);
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        // Control: the app is back and signs in afresh.
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);
    }

    [Fact]
    public async Task N22_a_session_is_401_after_its_apps_client_was_replaced()
    {
        await using GatewayHarness harness = await SignedIn.StartAsync();
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);

        // A new client at the provider, its secret where the gateway reads it, and the entry pointed at
        // both: an operator's client rotation (task 3.6).
        string clientId = app.ClientId + "-replaced";
        string secretRef = "ssm:/spa-platform/harness/apps/app1/client-secret-replaced:1";
        string secret = "secret-replaced-" + Guid.NewGuid().ToString("N");
        harness.Idp.AddClient(new TestIdpClient(
            clientId,
            secret,
            harness.Idp.Clients[app.ClientId].RedirectUris,
            harness.Idp.Clients[app.ClientId].PostLogoutUris));
        harness.Secrets[secretRef] = secret;

        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(document =>
        {
            JsonObject oauth = Entry(document, "app1")["oauth"]!.AsObject();
            oauth["clientId"] = clientId;
            oauth["clientSecretRef"] = secretRef;
        }, Suite.Ct));

        BrowserResponse session = await SignedIn.SessionAsync(browser);
        BrowserResponse api = await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        // Control: a new login goes to the new client and is a session again.
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(clientId, harness.Idp.Authorizations[^1].ClientId);
        Assert.Equal(HttpStatusCode.OK, (await SignedIn.SessionAsync(browser)).StatusCode);
    }

    private static JsonNode Entry(JsonObject document, string appId) =>
        document["apps"]!.AsArray().Single(entry => entry!["appId"]!.GetValue<string>() == appId)!;
}
