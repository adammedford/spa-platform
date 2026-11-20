using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Fixtures.Tests;

/// <summary>
/// The reference client's fragment surface (#998, part of #961): <c>api()</c> on
/// <c>/fragments/{fragmentId}/api/**</c>, the 401 read by its <c>code</c>, the <c>fragments</c> map
/// from <c>session()</c>, and <c>fragmentLogin()</c>. The seam is <c>window.BffClient</c> as the rest
/// of this class observes it: the requests it makes and the navigations it starts.
/// </summary>
public sealed partial class BffClientTests
{
    private const string FragmentApi = "/fragments/orders/api/orders";

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task An_unsafe_call_on_a_fragment_api_carries_the_apps_one_token(string method)
    {
        // "one token for the app, its fragments' APIs included" (Request headers the SPA sends).
        // Mutation: apiUrl accepting /api/** only turns this red, since the call is refused.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");

        JsonElement status = await harness.CallAsync($"BffClient.api('{FragmentApi}/echo', {{ method: '{method}', body: '{{}}' }}).then(r => r.status)");

        Assert.Equal(200, status.GetInt32());
        IRequest request = Assert.Single(harness.RequestsTo(FragmentApi + "/echo"));
        Assert.Equal(method, request.Method);
        Assert.Equal(Token, HeaderOf(request, Contract.Header));
    }

    [Theory]
    [MemberData(nameof(SafeMethods))]
    public async Task A_safe_call_on_a_fragment_api_carries_no_token(string method)
    {
        // Mutation: dropping the safe-method test from api() (every method gets the header) turns
        // this red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");

        await harness.CallAsync($"BffClient.api('{FragmentApi}/whoami', {{ method: '{method}' }}).then(r => r.status)");

        Assert.Null(HeaderOf(Assert.Single(harness.RequestsTo(FragmentApi + "/whoami")), Contract.Header));
    }

    [Fact]
    public async Task A_path_that_resolves_into_a_fragment_api_is_sent_as_it_resolves()
    {
        // The check is on the resolved path, the one the browser sends, not on the string.
        // Mutation: checking the raw string's prefix instead of the resolved path turns this red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);

        await harness.CallAsync($"BffClient.api('/api/../{FragmentApi.TrimStart('/')}/whoami').then(r => r.status)");

        Assert.Single(harness.RequestsTo(FragmentApi + "/whoami"));
    }

    [Theory]
    [InlineData("/fragments/a/api/../../bff/logout")]
    [InlineData("/fragments/orders/api/../../bff/logout")]
    [InlineData("/fragments/orders/entry.js")]
    [InlineData("/fragments/orders/assets/panel.js")]
    [InlineData("/fragments/orders/api")]
    [InlineData("/fragments//api/orders/x")]
    [InlineData("/fragments/orders/extra/api/orders/x")]
    [InlineData("/fragments/Orders/api/orders/x")]
    [InlineData("/fragments/or/api/orders/x")]
    [InlineData("/fragments/or_ders/api/orders/x")]
    [InlineData("https://elsewhere.test/fragments/orders/api/orders/x")]
    [InlineData("//elsewhere.test/fragments/orders/api/orders/x")]
    public async Task The_client_sends_nothing_outside_api_or_a_fragments_api(string path)
    {
        // "anything that does not resolve to one of those two prefixes on this origin is still
        // rejected before a request exists". The id segment is held to the fragmentId rule, so a
        // segment the registry could never list is refused here rather than sent with the token.
        // Mutations: accepting any /fragments/ path (red on entry.js, assets and logout), and
        // accepting any non-empty id segment (red on Orders, or and or_ders).
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");
        int before = harness.Requests.Count;

        JsonElement outcome = await harness.CallAsync($"BffClient.api('{path}', {{ method: 'POST' }}).then(() => 'sent', e => e.name)");

        Assert.Equal("TypeError", outcome.GetString());
        Assert.Equal(before, harness.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(SessionKeptCodes))]
    public async Task A_401_that_keeps_the_session_is_resolved_untouched_and_keeps_the_token(string code)
    {
        // fragment_login_required: the fragment has no usable token set, the shell's session stands
        // (Errors). No navigation, the antiforgery token kept, and the caller can still read the body.
        // Mutation: calling login() on every 401, as the client did before #998, turns this red (the
        // navigation destroys the page the call was evaluated in, and /bff/login is requested).
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42");
        harness.Session = StubAnswer.Ok(SignedIn());
        await harness.CallAsync("BffClient.session()");
        harness.Api = request => request.Url.Contains("/fragments/", StringComparison.Ordinal)
            ? StubAnswer.Problem(401, code)
            : StubAnswer.Ok(new { ok = true });

        JsonElement answer = await harness.CallAsync($"BffClient.api('{FragmentApi}/whoami').then(r => r.json().then(b => ({{ status: r.status, code: b.code }})))");

        // A second call after the first, so a navigation the first started has had its turn.
        JsonElement status = await harness.CallAsync("BffClient.api('/api/orders/echo', { method: 'POST' }).then(r => r.status)");

        Assert.Equal(401, answer.GetProperty("status").GetInt32());
        Assert.Equal(code, answer.GetProperty("code").GetString());
        Assert.Equal(200, status.GetInt32());
        Assert.Empty(harness.RequestsTo("/bff/login"));
        Assert.Equal(BffClientPage.Origin + "/orders/42", harness.Page.Url);

        // Mutation: clearing the token on every 401 turns these red: the POST reads the session again.
        Assert.Single(harness.RequestsTo("/bff/session"));
        Assert.Equal(Token, HeaderOf(Assert.Single(harness.RequestsTo("/api/orders/echo")), Contract.Header));
    }

    [Theory]
    [MemberData(nameof(LoginCodes))]
    public async Task A_401_that_ends_the_session_on_a_fragment_route_logs_in_again(string code)
    {
        // session_ended on a fragment's route is the shell's set refused: the session is gone, so
        // the way back is the ordinary login. Mutation: reading the code from the wrong field
        // (body.error) turns this red, since no code then starts a login.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42?tab=lines");
        harness.Api = _ => StubAnswer.Problem(401, code);

        IRequest login = await harness.NavigationFromAsync($"BffClient.api('{FragmentApi}/whoami')");

        AssertLogin(login, "/orders/42?tab=lines");
    }

    [Fact]
    public async Task A_401_with_a_code_the_client_does_not_know_starts_no_login()
    {
        // "starts login() only for unauthenticated or session_ended". Mutation: logging in on any
        // 401 whose code is not fragment_login_required turns this red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42");
        harness.Api = _ => StubAnswer.Problem(401, "some_future_code");

        JsonElement status = await harness.CallAsync("BffClient.api('/api/orders/whoami').then(r => r.status)");
        await harness.CallAsync("BffClient.api('/api/orders/whoami').then(r => r.status)");

        Assert.Equal(401, status.GetInt32());
        Assert.Empty(harness.RequestsTo("/bff/login"));
    }

    [Fact]
    public async Task A_401_with_a_body_that_is_not_json_resolves_untouched_and_starts_no_login()
    {
        // A 401 whose body has no code, as a HEAD call's 401 has none: the client still resolves the
        // Response to its caller, as "resolves to the upstream's Response, whatever its status" asks,
        // and starts nothing. Mutation: dropping codeOf's rejection handler turns this red, since
        // the failed parse then rejects the caller's promise.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42");
        harness.Api = _ => new StubAnswer(401);

        JsonElement status = await harness.CallAsync($"BffClient.api('{FragmentApi}/whoami').then(r => r.status, e => e.name)");
        await harness.CallAsync("BffClient.api('/api/orders/whoami').then(r => r.status, e => e.name)");

        Assert.Equal(401, status.GetInt32());
        Assert.Empty(harness.RequestsTo("/bff/login"));
    }

    [Fact]
    public async Task Session_returns_the_fragments_map_and_never_the_token()
    {
        // GET /bff/session -> fragments: every fragment of the current entry, by state. The client
        // hands it on, and never starts a fragment login on its own (#961, "The fallback is
        // user-initiated"). Mutation: leaving fragments out of session()'s answer turns this red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        Dictionary<string, object> body = SignedIn();
        body["fragments"] = new Dictionary<string, string>
        {
            ["orders"] = "signedIn",
            ["reports"] = "loginRequired",
            ["audit"] = "unavailable",
        };
        harness.Session = StubAnswer.Ok(body);

        JsonElement session = await harness.CallAsync("BffClient.session()");
        await harness.CallAsync("BffClient.session()");

        JsonElement fragments = session.GetProperty("fragments");
        Assert.Equal("signedIn", fragments.GetProperty("orders").GetString());
        Assert.Equal("loginRequired", fragments.GetProperty("reports").GetString());
        Assert.Equal("unavailable", fragments.GetProperty("audit").GetString());
        Assert.Equal("user-1", session.GetProperty("sub").GetString());
        Assert.False(session.TryGetProperty(Contract.TokenField, out _), $"session() returned {Contract.TokenField}.");
        Assert.DoesNotContain(Token, session.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(harness.RequestsTo("/bff/login"));
    }

    [Fact]
    public async Task Session_returns_an_empty_fragments_map_when_the_body_carries_none()
    {
        // "{} when the body carries none". Mutation: passing body.fragments through as it is (so
        // undefined, which JSON drops) turns this red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
        harness.Session = StubAnswer.Ok(SignedIn());

        // Serialised in the page: Playwright's own transfer marks an empty object with an "$id".
        JsonElement fragments = await harness.CallAsync("BffClient.session().then(s => JSON.stringify(s.fragments))");

        Assert.Equal("{}", fragments.GetString());
    }

    [Theory]
    [InlineData("'/reports/q3?year=2026#top'", "/reports/q3?year=2026#top")]
    [InlineData("undefined", "/orders/42?tab=lines")]
    [InlineData("'https://elsewhere.test/steal'", "/")]
    [InlineData("'//elsewhere.test/steal'", "/")]
    [InlineData("'/\\\\elsewhere.test/steal'", "/")]
    [InlineData("'reports'", "/")]
    public async Task FragmentLogin_navigates_to_bff_login_for_that_fragment_with_a_local_return_path(string argument, string expected)
    {
        // GET /bff/login?fragment={fragmentId}&returnUrl=/path, a navigation, with returnUrl held to
        // a local path by login()'s rule. Mutation: passing returnUrl through without localPath turns
        // the three foreign cases red.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42?tab=lines");

        IRequest login = await harness.NavigationFromAsync($"BffClient.fragmentLogin('reports', {argument})");

        AssertLogin(login, expected);
        Assert.Equal("reports", QueryHelpers.ParseQuery(new Uri(login.Url).Query)["fragment"].ToString());
    }

    [Theory]
    [MemberData(nameof(RefusedFragmentIds))]
    public async Task FragmentLogin_refuses_an_id_outside_the_fragmentId_rule(string argument)
    {
        // The rule is registry.schema.json's fragmentId, and each case breaks it one way. Mutation:
        // dropping the id check turns every case red, since each then navigates.
        await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser, "/orders/42");

        JsonElement outcome = await harness.CallAsync($"(() => {{ try {{ BffClient.fragmentLogin({argument}, '/'); return 'navigated'; }} catch (e) {{ return e.name; }} }})()");

        Assert.Equal("TypeError", outcome.GetString());
        Assert.Empty(harness.RequestsTo("/bff/login"));
    }

    [Fact]
    public async Task The_fragmentId_rule_the_refusals_break_is_the_registrys()
    {
        // The refused ids above are written against a rule; this reads that rule from the contract
        // and accepts its longest and shortest ids, so a client stricter than the registry fails here.
        // Mutations: the client's rule at {4,40}, and at {3,39}, each turn this red.
        JsonObject rule = FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"))["$defs"]!["fragmentId"]!.AsObject();
        string shortest = new('a', rule["minLength"]!.GetValue<int>());
        string longest = new string('9', rule["maxLength"]!.GetValue<int>() - 1) + "-";
        Assert.Matches(rule["pattern"]!.GetValue<string>(), shortest);

        foreach (string id in new[] { shortest, longest })
        {
            await using BffClientPage harness = await BffClientPage.OpenAsync(headless.Browser);
            IRequest login = await harness.NavigationFromAsync($"BffClient.fragmentLogin('{id}', '/')");
            Assert.Equal(id, QueryHelpers.ParseQuery(new Uri(login.Url).Query)["fragment"].ToString());
        }
    }

    public static TheoryData<string> SessionKeptCodes() => [.. Contract.SessionKeptCodes];

    /// <summary>Ids the fragmentId rule refuses: too short, too long, a capital, an underscore, a path, a query, none.</summary>
    public static TheoryData<string> RefusedFragmentIds()
    {
        JsonObject rule = FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"))["$defs"]!["fragmentId"]!.AsObject();
        int min = rule["minLength"]!.GetValue<int>();
        int max = rule["maxLength"]!.GetValue<int>();

        return
        [
            $"'{new string('a', min - 1)}'",
            $"'{new string('a', max + 1)}'",
            "'Orders'",
            "'or_ders'",
            "'../x'",
            "'orders&returnUrl=//evil.test'",
            "undefined",
        ];
    }
}
