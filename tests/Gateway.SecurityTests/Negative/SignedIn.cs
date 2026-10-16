using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// What the session-bearing rows of plan row 6.3 share: a cell that requires a session, the session
/// cookie read and written as the gateway's own cookie handler reads and writes it, and a login
/// stopped at the callback so a test can replay, alter or redirect it.
///
/// <para><b>Why the test reads the cookie with the gateway's own format.</b> N-02 and N-21 are about
/// what happens <i>after</i> decryption succeeds: "the store is bound to app and client", and
/// "decryption succeeds; the store lookup does not". A cookie a test could not decrypt would prove
/// only that a forgery fails to decrypt, which is a different and weaker claim. The format is the one
/// the framework built for the app's cookie scheme (<see cref="SchemeNames.Cookie"/>), read through
/// <see cref="IOptionsMonitor{TOptions}"/>, so nothing here reimplements it. With a session store the
/// cookie holds only a ticket naming the session's key
/// (https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Cookies/src/CookieAuthenticationHandler.cs,
/// <c>SessionIdClaim</c>), which is why <see cref="KeyOf"/> takes the ticket's one claim.</para>
/// </summary>
internal static class SignedIn
{
    /// <summary>Follow no redirect, so a test sees each hop of a login.</summary>
    public static readonly RequestOptions Manual = new() { Redirect = RedirectMode.Manual };

    /// <summary>The session cookie's name, from the Cookies table of the browser contract.</summary>
    public static string SessionCookieName { get; } = Contract.PlatformCookie("-session");

    /// <summary>The antiforgery cookie's name, from the same table.</summary>
    public static string CsrfCookieName { get; } = Contract.PlatformCookie("-csrf");

    /// <summary>A cell with a session required, as the shipped gateway requires one, enrolled in the canary sweep.</summary>
    public static Task<GatewayHarness> StartAsync(GatewayHarnessOptions? options = null) =>
        Suite.StartAsync((options ?? new GatewayHarnessOptions()) with { SessionRequired = true }, Suite.Ct);

    /// <summary>
    /// Puts the browser on a page of the app without starting a login: <c>/healthz</c> answers on any
    /// host before host resolution and sets nothing (contracts/pipeline.md). A navigation to the shell
    /// would be challenged, and the test provider approves at once, so it would sign the browser in.
    /// </summary>
    public static async Task OnPageOfAsync(TestBrowser browser, HarnessApp app)
    {
        BrowserResponse page = await browser.NavigateAsync(app.Url("/healthz"), Manual, Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    /// <summary>The browser's live session cookie for the app.</summary>
    public static string SessionCookie(TestBrowser browser, HarnessApp app) =>
        Assert.Single(browser.Cookies.GetCookies(app.Origin), cookie => !cookie.Expired && cookie.Name == SessionCookieName).Value;

    /// <summary>Whether the browser holds a live session cookie for the app.</summary>
    public static bool HoldsSessionCookie(TestBrowser browser, HarnessApp app) =>
        browser.Cookies.GetCookies(app.Origin).Any(cookie => !cookie.Expired && cookie.Name == SessionCookieName);

    /// <summary>The ticket in a session cookie, decrypted with the app's own cookie format, or null when it does not decrypt.</summary>
    public static AuthenticationTicket? Unprotect(GatewayHarness harness, string appId, string cookieValue) =>
        Format(harness, appId).Unprotect(cookieValue);

    /// <summary>A session cookie for <paramref name="appId"/> carrying <paramref name="ticket"/>, encrypted as that app's cookie handler encrypts.</summary>
    public static string Protect(GatewayHarness harness, string appId, AuthenticationTicket ticket) =>
        Format(harness, appId).Protect(ticket);

    /// <summary>The session key a cookie names, after decrypting it with the app's format. Fails when it does not decrypt.</summary>
    public static string KeyOf(GatewayHarness harness, string appId, string cookieValue)
    {
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(Unprotect(harness, appId, cookieValue));
        return Assert.Single(ticket.Principal.Claims).Value;
    }

    /// <summary>The gateway's session store.</summary>
    public static ISessionStore Store(GatewayHarness harness) => harness.Services.GetRequiredService<ISessionStore>();

    /// <summary>
    /// <c>GET /bff/session</c> from the browser's current page, with an optional <c>Cookie</c> header in
    /// place of the jar's.
    /// </summary>
    public static Task<BrowserResponse> SessionAsync(TestBrowser browser, string? cookie = null) =>
        browser.GetAsync("/bff/session", cookie is null ? null : RequestOptions.With(("Cookie", cookie)), Suite.Ct);

    /// <summary>One string property of a JSON body.</summary>
    public static string? Property(BrowserResponse response, string name)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>The <c>csrfToken</c> a signed-in browser's <c>/bff/session</c> hands out (contracts/http-contract.md).</summary>
    public static async Task<string> CsrfTokenAsync(TestBrowser browser)
    {
        BrowserResponse session = await SessionAsync(browser);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        return Assert.IsType<string>(Property(session, "csrfToken"));
    }

    /// <summary>
    /// Starts a login to the app and stops at the callback: <c>/bff/login</c>, then the provider's
    /// authorize endpoint, which answers with a redirect to <c>/signin-oidc</c> carrying the code and
    /// state. The browser holds the login-state cookies; the callback has not been requested.
    /// </summary>
    public static async Task<Uri> UpToCallbackAsync(GatewayHarness harness, TestBrowser browser, HarnessApp app)
    {
        BrowserResponse login = await browser.NavigateAsync(app.Url("/bff/login"), Manual, Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Uri authorize = new(login.Header("Location")!);
        Assert.Equal(harness.Idp.AuthorizeEndpoint, authorize.GetLeftPart(UriPartial.Path));

        BrowserResponse approved = await browser.ClickAsync(authorize.ToString(), Manual, Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        Uri callback = new(approved.Header("Location")!);
        Assert.Equal(app.Url("/signin-oidc"), new Uri(callback.GetLeftPart(UriPartial.Path)));
        return callback;
    }

    /// <summary>A query parameter of a URL, unescaped.</summary>
    public static string Query(Uri url, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query).TryGetValue(name, out var value)
            ? value.ToString()
            : throw new InvalidOperationException($"{url.GetLeftPart(UriPartial.Path)} carries no {name}.");

    /// <summary>The same callback with one query parameter replaced.</summary>
    public static Uri WithQuery(Uri url, string name, string value)
    {
        Dictionary<string, string?> query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.Ordinal);
        query[name] = value;
        return new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url.GetLeftPart(UriPartial.Path), query));
    }

    /// <summary>The <c>Cookie</c> header the browser would send to the app, without the cookies <paramref name="leaveOut"/> picks.</summary>
    public static string CookieHeader(TestBrowser browser, HarnessApp app, Func<string, bool>? leaveOut = null) =>
        string.Join("; ", browser.Cookies.GetCookies(app.Origin)
            .Where(cookie => !cookie.Expired && !(leaveOut?.Invoke(cookie.Name) ?? false))
            .Select(cookie => cookie.Name + "=" + cookie.Value));

    private static ISecureDataFormat<AuthenticationTicket> Format(GatewayHarness harness, string appId) =>
        harness.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SchemeNames.Cookie(appId)).TicketDataFormat
        ?? throw new InvalidOperationException($"the cookie scheme of {appId} has no ticket format.");
}
