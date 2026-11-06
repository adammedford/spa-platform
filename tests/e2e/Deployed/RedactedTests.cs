using Gateway.E2ETests.Stack;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// What a failure message prints for a login's URLs (<see cref="Redacted"/>): on the cell those carry
/// Cognito's state and the callback's authorization code, which AGENTS.md has printed nowhere.
/// </summary>
public sealed class RedactedTests
{
    private const string Code = "CODE-CANARY";
    private const string State = "STATE-CANARY";

    private static readonly Uri Authorize = new($"https://spa-poc.auth.us-east-1.amazoncognito.com/oauth2/authorize?client_id=c1&state={State}&nonce=n");
    private static readonly Uri Callback = new($"https://app1.spa-poc.test:8443/signin-oidc?code={Code}&state={State}#done");

    [Fact]
    public void A_url_prints_its_scheme_host_and_path_and_neither_its_query_nor_its_fragment()
    {
        Assert.Equal("https://app1.spa-poc.test:8443/signin-oidc", Redacted.Url(Callback));
        Assert.Equal("https://app1.spa-poc.test:8443/signin-oidc", Redacted.Url(Callback.ToString()));
    }

    [Fact]
    public void A_list_of_navigations_prints_no_code_and_no_state()
    {
        string printed = Redacted.Urls([Authorize, Callback]);

        Assert.Equal("https://spa-poc.auth.us-east-1.amazoncognito.com/oauth2/authorize -> https://app1.spa-poc.test:8443/signin-oidc", printed);
        Assert.DoesNotContain(Code, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(State, printed, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exchange_prints_its_url_with_no_query()
    {
        Exchange callback = new("GET", Callback, new Dictionary<string, string>(), 302, [], []);

        Assert.Equal("GET https://app1.spa-poc.test:8443/signin-oidc -> 302", callback.ToString());
    }

    [Fact]
    public void A_page_url_that_does_not_parse_is_not_printed()
    {
        string printed = Redacted.Url($"not a url ?code={Code}");

        Assert.DoesNotContain(Code, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_playwright_timeout_is_rethrown_as_a_timeout_whose_call_log_prints_no_query()
    {
        string log = $"""
            Timeout 30000ms exceeded.
            =========================== logs ===========================
            waiting for navigation to "https://app1.spa-poc.test:8443/" until "load"
              navigated to "{Authorize}"
              navigated to "{Callback}"
            ============================================================
            """;

        System.TimeoutException thrown = await Assert.ThrowsAsync<System.TimeoutException>(
            () => Redacted.PlaywrightAsync(() => throw new System.TimeoutException(log)));

        Assert.DoesNotContain(Code, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(State, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("navigated to \"https://spa-poc.auth.us-east-1.amazoncognito.com/oauth2/authorize\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("navigated to \"https://app1.spa-poc.test:8443/signin-oidc\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Timeout 30000ms exceeded.", thrown.Message, StringComparison.Ordinal);
        Assert.Null(thrown.InnerException);
    }

    [Fact]
    public async Task A_playwright_failure_that_is_not_a_timeout_keeps_its_type_and_its_value_comes_back_when_none_is_thrown()
    {
        PlaywrightException thrown = await Assert.ThrowsAsync<PlaywrightException>(
            () => Redacted.PlaywrightAsync<int>(() => throw new PlaywrightException($"net::ERR_ABORTED at {Callback}")));

        Assert.Equal("net::ERR_ABORTED at https://app1.spa-poc.test:8443/signin-oidc", thrown.Message);
        Assert.Equal(42, await Redacted.PlaywrightAsync(() => Task.FromResult(42)));
    }

    // ---- a page's text, as a timed-out hold records it (#1003) ----

    private const string Jwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJjYW5hcnkifQ.c2lnbmF0dXJl";

    [Fact]
    public void A_pages_text_loses_the_values_it_is_given_every_urls_query_and_every_jwt_and_keeps_the_rest()
    {
        string text = Redacted.Text($"Sign in. state {State} is stale; go to {Callback} with {Jwt}. en", [State, "en"]);

        Assert.Equal($"Sign in. state {Redacted.Removed} is stale; go to https://app1.spa-poc.test:8443/signin-oidc with {Redacted.Removed}. en", text);
    }

    [Fact]
    public void A_pages_text_is_cut_at_its_bound()
    {
        string text = Redacted.Text(new string('a', Redacted.MaxText + 10), []);

        Assert.Equal(new string('a', Redacted.MaxText) + " (cut)", text);
        Assert.Equal("short", Redacted.Text("short", []));
    }

    [Fact]
    public void A_urls_parameter_values_come_from_its_query_and_its_fragment_as_written_and_decoded()
    {
        string[] values = [.. Redacted.ParameterValues($"https://app1.spa-poc.test:8443/signin-oidc?code={Code}&state=a%2Bb#id_token={Jwt}")];

        Assert.Equal([Code, Code, "a%2Bb", "a+b", Jwt, Jwt], values);
        Assert.Empty(Redacted.ParameterValues($"not a url ?code={Code}"));
    }

    [Fact]
    public async Task A_timed_out_hold_is_rethrown_as_one_and_keeps_what_the_page_showed()
    {
        PageShown page = new("https://spa-poc.auth.us-east-1.amazoncognito.com/login", "Sign in with your username and password");

        HoldTimeoutException thrown = await Assert.ThrowsAsync<HoldTimeoutException>(
            () => Redacted.PlaywrightAsync(() => throw new HoldTimeoutException($"no request came; the page is at {Authorize}", page)));

        Assert.Equal("no request came; the page is at https://spa-poc.auth.us-east-1.amazoncognito.com/oauth2/authorize", thrown.Message);
        Assert.Equal(page, thrown.Page);
    }
}
