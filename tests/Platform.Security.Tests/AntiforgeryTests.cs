using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Security.Tests;

/// <summary>
/// <c>UsePlatformAntiforgery</c>: unsafe methods on <c>/api</c> need a valid <c>X-CSRF-TOKEN</c>,
/// bound to the user, the cookie token and the app, and read from the header only. Task 4.4; N-06 and
/// the evidence rows R-E1, R-E2, R-E3b, R-E5 and R-E6 (<c>docs/plan/07-evidence.md</c>), with findings
/// R-E3, R-E4 and R-E7 as the behaviour each setting prevents.
/// </summary>
public sealed class AntiforgeryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Platform.Abstractions.AppDefinition App1 = AntiforgeryHost.App1;

    private static Task AssertRefusedAsync(AntiforgeryHost host, HttpResponseMessage response, params string[] canaries) =>
        AssertRefusedAfterAsync(host, response, reachedBefore: 0, canaries);

    /// <param name="reachedBefore">How many earlier requests in the test reached the terminal, as a control.</param>
    private static async Task AssertRefusedAfterAsync(AntiforgeryHost host, HttpResponseMessage response, int reachedBefore, params string[] canaries)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(403, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("csrf_failed", body.RootElement.GetProperty("code").GetString());

        // The terminal sends Access-Control-Allow-Origin and counts itself, so either fails if the
        // request got past the antiforgery check.
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(reachedBefore, host.Reached);
        Assert.Single(host.Log.LinesFor("csrf.token"));

        // N-16: neither half of the pair is ever logged.
        foreach (string canary in canaries)
        {
            Assert.False(host.Log.Contains(canary), "a token or cookie value was logged");
        }
    }

    private static async Task AssertPassedAsync(AntiforgeryHost host, HttpResponseMessage response, string body = "")
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("passed:" + body, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, host.Reached);
    }

    // ---- N-06 and R-E1: no token ----

    [Fact]
    public async Task N06_an_unsafe_api_call_with_the_cookie_and_no_header_token_gets_403_csrf_failed()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: issued.Cookie, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response, issued.RequestToken, issued.Cookie!);
    }
    [Fact]
    public async Task N06_an_unsafe_api_call_with_neither_cookie_nor_token_gets_403_csrf_failed()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("DELETE", "/api/orders/items/7", App1, "alice", Ct);

        await AssertRefusedAsync(host, response);
    }

    [Fact]
    public async Task N06_an_unsafe_api_call_with_no_session_gets_403_csrf_failed()
    {
        // No anonymous pair exists to pass: /bff/session issues tokens to a signed-in user only. This
        // pins the decision that antiforgery checks the anonymous request rather than leaving it to the
        // endpoint.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, user: null, Ct,
            cookie: issued.Cookie, csrfHeader: issued.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response, issued.RequestToken, issued.Cookie!);
    }

    [Theory]
    [InlineData("/API/orders/items")]
    [InlineData("/Api/orders")]
    [InlineData("/api")]
    public async Task N06_the_api_prefix_is_matched_in_any_letter_case(string path)
    {
        // Endpoint routing matches /API like /api, so a case-sensitive check here would be a way past it.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", path, App1, "alice", Ct, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response);
    }

    // ---- R-E1: issued by the session endpoint, accepted from the header ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task RE1_the_token_the_session_endpoint_issued_is_accepted_from_the_header_and_the_body_is_left_whole(string method)
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync(method, "/api/orders/items", App1, "alice", Ct,
            cookie: issued.Cookie, csrfHeader: issued.RequestToken, content: AntiforgeryHost.Json("{\"order\":7}"));

        await AssertPassedAsync(host, response, "{\"order\":7}");
        Assert.Equal(0, host.LastBodyBytesReadBeforeTerminal);
    }

    [Theory]
    [InlineData("GET", "/api/orders/items")]
    [InlineData("HEAD", "/api/orders/items")]
    [InlineData("OPTIONS", "/api/orders/items")]
    [InlineData("POST", "/bff/logout")]
    [InlineData("GET", "/index.html")]
    public async Task A_safe_method_or_a_path_outside_api_needs_no_token(string method, string path)
    {
        // The other half of N-06. /bff/logout is a form post, which cannot send a header; the request
        // gate's Origin check covers it instead (ADR-0007).
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync(method, path, App1, "alice", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(host.Log.LinesFor("csrf.token"));
    }

    // ---- N-06: another user's token ----

    [Fact]
    public async Task N06_another_users_request_token_with_your_own_cookie_gets_403()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued alice = await host.IssueAsync(App1, "alice", Ct);
        AntiforgeryHost.Issued bob = await host.IssueAsync(App1, "bob", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "bob", Ct,
            cookie: bob.Cookie, csrfHeader: alice.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response, alice.RequestToken, bob.RequestToken, alice.Cookie!, bob.Cookie!);
    }

    [Fact]
    public async Task N06_another_users_whole_pair_gets_403()
    {
        // The token is bound to the user's sub: alice's cookie and request token, sent in bob's session.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued alice = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "bob", Ct,
            cookie: alice.Cookie, csrfHeader: alice.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response, alice.RequestToken, alice.Cookie!, "alice");
    }

    // ---- R-E2 and R-E3b: another app's token ----

    [Fact]
    public async Task RE2_app1s_request_token_with_app2s_cookie_gets_403_on_app2()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued onApp1 = await host.IssueAsync(App1, "alice", Ct);
        AntiforgeryHost.Issued onApp2 = await host.IssueAsync(AntiforgeryHost.App2, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", AntiforgeryHost.App2, "alice", Ct,
            cookie: onApp2.Cookie, csrfHeader: onApp1.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAsync(host, response, onApp1.RequestToken, onApp2.Cookie!);
    }

    [Fact]
    public async Task RE3b_app1s_whole_pair_is_accepted_on_app1_and_refused_on_app2()
    {
        // One host serves both apps, so one key ring signed the pair: the refusal is the app binding's.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued onApp1 = await host.IssueAsync(App1, "alice", Ct);

        using (HttpResponseMessage control = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: onApp1.Cookie, csrfHeader: onApp1.RequestToken, content: AntiforgeryHost.Json("{}")))
        {
            await AssertPassedAsync(host, control, "{}");
        }

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", AntiforgeryHost.App2, "alice", Ct,
            cookie: onApp1.Cookie, csrfHeader: onApp1.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAfterAsync(host, response, reachedBefore: 1, onApp1.RequestToken, onApp1.Cookie!);
    }

    [Fact]
    public async Task RE3_without_the_app_binding_app1s_pair_validates_on_app2()
    {
        // Finding R-E3, reproduced by removing the binding: this is what makes RE3b able to fail.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct,
            services => services.Replace(ServiceDescriptor.Singleton<IAntiforgeryAdditionalDataProvider, NoAppBinding>()));
        AntiforgeryHost.Issued onApp1 = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", AntiforgeryHost.App2, "alice", Ct,
            cookie: onApp1.Cookie, csrfHeader: onApp1.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertPassedAsync(host, response, "{}");
    }

    // ---- N-06 and R-E4: a token from before the last logout ----

    [Fact]
    public async Task Logout_deletes_the_cookie_with_the_attributes_a_browser_needs_to_honour_it()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/bff/logout", App1, "alice", Ct, cookie: issued.Cookie);

        // A browser removes a __Host- cookie only when the deletion is itself a valid __Host- cookie.
        string deletion = AntiforgeryHost.CsrfSetCookie(response) ?? throw new InvalidOperationException("logout set no __Host-spa-csrf.");
        Assert.StartsWith("__Host-spa-csrf=;", deletion, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", deletion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", deletion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", deletion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", deletion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task N06_a_token_from_before_the_last_logout_gets_403_in_the_next_session()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued before = await host.IssueAsync(App1, "alice", Ct);

        using (HttpResponseMessage logout = await host.SendAsync("POST", "/bff/logout", App1, "alice", Ct, cookie: before.Cookie))
        {
            Assert.NotNull(AntiforgeryHost.CsrfSetCookie(logout));
        }

        // The browser honoured the deletion, so the next session's /bff/session sends no cookie and gets
        // a new one.
        AntiforgeryHost.Issued after = await host.IssueAsync(App1, "alice", Ct);
        Assert.NotNull(after.SetCookie);
        Assert.NotEqual(before.Cookie, after.Cookie);

        using (HttpResponseMessage control = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: after.Cookie, csrfHeader: after.RequestToken, content: AntiforgeryHost.Json("{}")))
        {
            await AssertPassedAsync(host, control, "{}");
        }

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: after.Cookie, csrfHeader: before.RequestToken, content: AntiforgeryHost.Json("{}"));

        await AssertRefusedAfterAsync(host, response, reachedBefore: 1, before.RequestToken, after.RequestToken, before.Cookie!, after.Cookie!);
    }

    // ---- R-E5: the cookie a browser will keep ----

    [Fact]
    public async Task RE5_the_cookie_is_host_prefixed_secure_httponly_strict_on_path_root_with_no_domain()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        string setCookie = issued.SetCookie ?? throw new InvalidOperationException("no __Host-spa-csrf was set.");
        Assert.Contains("; secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RE5_with_the_framework_default_policy_a_plain_http_request_gets_the_cookie_without_secure()
    {
        // The finding behind SecurePolicy = Always, reproduced by putting the default back. A browser
        // discards this cookie, so the SPA's every unsafe call would fail.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct,
            services => services.PostConfigure<AntiforgeryOptions>(o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest));

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/session", App1, "alice", Ct, https: false);

        string setCookie = AntiforgeryHost.CsrfSetCookie(response) ?? throw new InvalidOperationException("no __Host-spa-csrf was set.");
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RE5_the_platform_never_sets_the_cookie_on_a_request_that_did_not_arrive_as_https()
    {
        // With Always, the framework refuses to run on a request whose scheme is http
        // (DefaultAntiforgery.CheckSSLConfig): no cookie without Secure can leave. This is why
        // UsePlatformForwardedHeaders must apply X-Forwarded-Proto first.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/session", App1, "alice", Ct, https: false);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(AntiforgeryHost.CsrfSetCookie(response));
    }

    // ---- R-E6 and R-E7: header tokens only, and the body is never read ----

    [Fact]
    public async Task RE6_a_form_field_token_is_refused_and_the_body_is_not_read()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: issued.Cookie, content: AntiforgeryHost.FormWithToken(issued.RequestToken));

        await AssertRefusedAsync(host, response, issued.RequestToken, issued.Cookie!);
        Assert.Equal(0, host.LastBodyBytesReadBeforeTerminal);
    }

    [Fact]
    public async Task RE6_a_header_token_on_a_form_post_is_accepted_and_the_whole_form_reaches_the_terminal()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct);
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);
        using FormUrlEncodedContent form = AntiforgeryHost.FormWithToken("not-a-token");
        string sent = await form.ReadAsStringAsync(Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: issued.Cookie, csrfHeader: issued.RequestToken, content: AntiforgeryHost.FormWithToken("not-a-token"));

        await AssertPassedAsync(host, response, sent);
        Assert.Equal(0, host.LastBodyBytesReadBeforeTerminal);
    }

    [Fact]
    public async Task RE7_with_form_reading_on_a_form_field_token_is_accepted_and_the_body_is_consumed()
    {
        // Finding R-E7, reproduced by turning suppression off: what makes the two RE6 tests able to fail.
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct,
            services => services.PostConfigure<AntiforgeryOptions>(o => o.SuppressReadingTokenFromFormBody = false));
        AntiforgeryHost.Issued issued = await host.IssueAsync(App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", App1, "alice", Ct,
            cookie: issued.Cookie, content: AntiforgeryHost.FormWithToken(issued.RequestToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(host.LastBodyBytesReadBeforeTerminal > 0, "the form body was not read before the terminal");
    }

    /// <summary>What the framework binds when nothing else does: nothing.</summary>
    private sealed class NoAppBinding : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context) => "";

        public bool ValidateAdditionalData(HttpContext context, string additionalData) => true;
    }
}
