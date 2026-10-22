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
}
