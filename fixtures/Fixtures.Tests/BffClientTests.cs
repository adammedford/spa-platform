using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Fixtures.Tests;

/// <summary>
/// Task 5.2: <c>fixtures/bff-client.js</c>, the reference client contracts/http-contract.md calls
/// it, proved in headless Chromium against stubbed <c>/bff</c> and <c>/api</c> endpoints.
///
/// The seam is the client's public surface, <c>window.BffClient</c>, observed where it meets the
/// gateway: the requests it makes and the navigations it starts. The header name, the session field
/// it reads, the safe methods and the 401 codes are read from the contract, never restated
/// (CODING_STANDARDS.md, "A guard reads the thing it guards").
/// </summary>
public sealed partial class BffClientTests(HeadlessBrowser headless) : IClassFixture<HeadlessBrowser>
{
    private const string Token = "csrf-canary-5f2b9a";

    [Fact]
    public async Task Session_returns_the_signed_in_user()
    {
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());

        JsonElement session = await harness.CallAsync("BffClient.session()");

        Assert.Equal("user-1", session.GetProperty("sub").GetString());
        Assert.Equal("Ada", session.GetProperty("name").GetString());
        Assert.Equal("ada@example.test", session.GetProperty("email").GetString());
        Assert.Equal("2026-10-13T12:00:00Z", session.GetProperty("expiresAt").GetString());

        IRequest request = Assert.Single(harness.RequestsTo("/bff/session"));
        Assert.Equal("GET", request.Method);
        Assert.False(request.IsNavigationRequest, "The session is fetched, not navigated to.");
    }

    [Fact]
    public async Task Session_is_null_without_a_session_and_does_not_navigate()
    {
        // 401 here is an answer, not a failure: the SPA decides whether to show a sign-in button or
        // to log in at once, so the client does not leave the page on its behalf.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Problem(401, Contract.UnauthorizedCodes[0]);

        JsonElement session = await harness.CallAsync("BffClient.session()");

        Assert.Equal(JsonValueKind.Null, session.ValueKind);
        Assert.DoesNotContain(harness.Requests, request => request.IsNavigationRequest && request.Url.Contains("/bff/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task An_unsafe_api_call_carries_the_token_from_the_session(string method)
    {
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");

        JsonElement status = await harness.CallAsync($"BffClient.api('/api/orders/echo', {{ method: '{method}', body: '{{}}' }}).then(r => r.status)");

        Assert.Equal(200, status.GetInt32());
        IRequest request = Assert.Single(harness.RequestsTo("/api/orders/echo"));
        Assert.Equal(method, request.Method);
        Assert.Equal(Token, HeaderOf(request, Contract.Header));
    }

    [Theory]
    [MemberData(nameof(SafeMethods))]
    public async Task A_safe_api_call_carries_no_token(string method)
    {
        // "Every unsafe method on /api/**" and no other: the token is not sent where it is not needed.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");

        JsonElement status = await harness.CallAsync($"BffClient.api('/api/orders/whoami', {{ method: '{method}' }}).then(r => r.status)");

        Assert.Equal(200, status.GetInt32());
        IRequest request = Assert.Single(harness.RequestsTo("/api/orders/whoami"));
        Assert.Null(HeaderOf(request, Contract.Header));
    }

    [Fact]
    public async Task An_unsafe_call_before_any_session_read_reads_the_session_first()
    {
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());

        await harness.CallAsync("BffClient.api('/api/orders/echo', { method: 'POST' }).then(r => r.status)");

        Assert.Single(harness.RequestsTo("/bff/session"));
        Assert.Equal(Token, HeaderOf(Assert.Single(harness.RequestsTo("/api/orders/echo")), Contract.Header));
    }

    [Theory]
    [InlineData("/bff/session")]
    [InlineData("/orders")]
    [InlineData("https://elsewhere.test/api/orders/echo")]
    [InlineData("//elsewhere.test/api/orders/echo")]
    [InlineData("/api/../bff/logout")]
    public async Task The_client_sends_nothing_outside_api(string path)
    {
        // The token travels to /api/** and nowhere else, so a path anywhere else is refused before
        // a request is made rather than sent without it.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");
        int before = harness.Requests.Count;

        JsonElement outcome = await harness.CallAsync($"BffClient.api('{path}', {{ method: 'POST' }}).then(() => 'sent', e => e.name)");

        Assert.Equal("TypeError", outcome.GetString());
        Assert.Equal(before, harness.Requests.Count);
    }

    [Fact]
    public async Task An_api_call_answers_with_the_upstreams_response()
    {
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Api = _ => new StubAnswer(201, new { id = 7 });

        JsonElement body = await harness.CallAsync("BffClient.api('/api/orders/whoami').then(r => r.json().then(b => ({ status: r.status, id: b.id })))");

        Assert.Equal(201, body.GetProperty("status").GetInt32());
        Assert.Equal(7, body.GetProperty("id").GetInt32());
    }

    [Theory]
    [MemberData(nameof(UnauthorizedCodes))]
    public async Task A_401_from_the_api_logs_in_again_and_comes_back_here(string code)
    {
        // Both 401s mean the same thing to the page, "no session": unauthenticated never had one, and
        // session_ended had its refresh token refused. Either way the way back is a fresh login that
        // returns to where the user was.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42?tab=lines");
        harness.Api = _ => StubAnswer.Problem(401, code);

        IRequest login = await harness.NavigationFromAsync("BffClient.api('/api/orders/whoami')");

        AssertLogin(login, "/orders/42?tab=lines");
    }

    [Fact]
    public async Task A_status_other_than_401_does_not_log_in()
    {
        // The case that makes the test above able to fail: 403 csrf_failed is the SPA's to handle.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Api = _ => StubAnswer.Problem(403, "csrf_failed");

        JsonElement status = await harness.CallAsync("BffClient.api('/api/orders/whoami').then(r => r.status)");

        Assert.Equal(403, status.GetInt32());
        Assert.Empty(harness.RequestsTo("/bff/login"));
    }

    [Theory]
    [InlineData("'/reports/q3?year=2026#top'", "/reports/q3?year=2026#top")]
    [InlineData("undefined", "/orders/42?tab=lines")]
    [InlineData("'https://elsewhere.test/steal'", "/")]
    [InlineData("'//elsewhere.test/steal'", "/")]
    [InlineData("'/\\\\elsewhere.test/steal'", "/")]
    [InlineData("'/\\t/elsewhere.test/steal'", "/")]
    [InlineData("'reports'", "/")]
    public async Task Login_navigates_to_bff_login_with_a_local_return_path(string argument, string expected)
    {
        // "returnUrl must be a local path, or it becomes /": the gateway enforces it, and the client
        // never asks for anything else.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42?tab=lines");

        IRequest login = await harness.NavigationFromAsync($"BffClient.login({argument})");

        AssertLogin(login, expected);
    }

    [Fact]
    public async Task Logout_is_a_form_post_that_carries_no_token()
    {
        // POST /bff/logout is a form navigation, so the gateway's redirect through the provider's
        // logout is followed by the browser. The token goes in a header or not at all, and a form
        // cannot set a header, so the body has nothing in it.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");

        IRequest logout = await harness.NavigationFromAsync("BffClient.logout()");

        Assert.Equal("/bff/logout", new Uri(logout.Url).AbsolutePath);
        Assert.Equal("POST", logout.Method);
        Assert.True(logout.IsNavigationRequest, "Logout is a navigation, not a fetch.");
        Assert.True(string.IsNullOrEmpty(logout.PostData), $"The logout form posted fields: {logout.PostData}");
        Assert.Null(HeaderOf(logout, Contract.Header));
    }

    [Fact]
    public async Task The_token_is_not_handed_to_the_page()
    {
        // F-05 dumps web storage and the page after login and expects nothing token-shaped.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());

        JsonElement session = await harness.CallAsync("BffClient.session()");
        string exposed = await harness.Page.EvaluateAsync<string>(
            "() => JSON.stringify([Object.keys(window), localStorage, sessionStorage, document.documentElement.outerHTML])");

        Assert.False(session.TryGetProperty(Contract.TokenField, out _), $"session() returned {Contract.TokenField}.");
        Assert.DoesNotContain(Token, session.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exposed, StringComparison.Ordinal);
    }

    public static TheoryData<string> UnauthorizedCodes() => [.. Contract.UnauthorizedCodes];

    private static void AssertLogin(IRequest login, string returnUrl)
    {
        Uri url = new(login.Url);
        Assert.Equal("/bff/login", url.AbsolutePath);
        Assert.Equal("GET", login.Method);
        Assert.True(login.IsNavigationRequest, "Login is a navigation, never a fetch.");
        Assert.Equal(returnUrl, QueryHelpers.ParseQuery(url.Query)["returnUrl"].ToString());
    }

    public static TheoryData<string> SafeMethods() => [.. Contract.SafeMethods];

    private static string? HeaderOf(IRequest request, string name) =>
        request.Headers.TryGetValue(name.ToLowerInvariant(), out string? value) ? value : null;

    private static Dictionary<string, object> SignedIn() => new()
    {
        ["sub"] = "user-1",
        ["name"] = "Ada",
        ["email"] = "ada@example.test",
        ["expiresAt"] = "2026-10-13T12:00:00Z",
        [Contract.TokenField] = Token,
    };

    /// <summary>The values the client must agree with, read out of contracts/http-contract.md.</summary>
    private static partial class Contract
    {
        private static readonly string Text = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));

        /// <summary>The row of "Request headers the SPA sends" that carries the antiforgery token.</summary>
        private static readonly Match HeaderRow = Require(
            HeaderRowPattern().Match(Text), "no request-header row reading its value from a session field");

        public static string Header => HeaderRow.Groups["header"].Value;

        public static string TokenField => HeaderRow.Groups["field"].Value;

        /// <summary>"anything but `GET`, `HEAD` and `OPTIONS`": the methods that carry no token.</summary>
        public static IReadOnlyList<string> SafeMethods =>
            [.. Regex.Matches(HeaderRow.Groups["on"].Value.Split("anything but")[1], "`([A-Z]+)`", RegexOptions.None, RegexTimeout.Value).Select(m => m.Groups[1].Value)];

        /// <summary>Every <c>code</c> the Errors table gives a 401.</summary>
        public static IReadOnlyList<string> UnauthorizedCodes =>
            [.. UnauthorizedRow().Matches(Text).Select(m => m.Groups[1].Value)];

        private static Match Require(Match match, string what) =>
            match.Success ? match : throw new InvalidOperationException($"contracts/http-contract.md: {what}.");

        [GeneratedRegex(@"^\| `(?<header>X-[A-Za-z-]+)` \| (?<on>[^|]+) \| [^|]*`(?<field>[A-Za-z]+)` in the `GET /bff/session` response", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
        private static partial Regex HeaderRowPattern();

        [GeneratedRegex(@"^\| 401 \| `([a-z_]+)` \|", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
        private static partial Regex UnauthorizedRow();
    }
}
