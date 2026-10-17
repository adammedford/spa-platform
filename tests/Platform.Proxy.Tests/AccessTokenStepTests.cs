using System.Net;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.2's first half: the token is acquired ahead of the forwarder, and a call with no token is
/// answered 401 with a problem body and reaches no upstream (N-19, the <c>/api</c> clause;
/// <c>/bff/session</c> is workstream 3's), or 503 when the provider could not be reached and the session
/// stands. The step is a pipeline step and not a YARP transform, so the 401 is an ordinary response
/// (plan, workstream 4; finding T-C21).
/// </summary>
public sealed class AccessTokenStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_with_no_session_is_401_unauthenticated_and_reaches_no_upstream()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, Orders(listener));
        gateway.Tokens.Result = new AccessTokenResult.Unauthenticated();
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthenticated", await Problem.CodeOf(response));
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task A_session_whose_refresh_was_refused_is_401_session_ended_and_reaches_no_upstream()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, Orders(listener));
        gateway.Tokens.Result = new AccessTokenResult.SessionEnded();
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Post, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("session_ended", await Problem.CodeOf(response));
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_is_503_idp_unavailable_and_reaches_no_upstream()
    {
        // contracts/http-contract.md -> Errors: the session is kept, so this is not one of the two 401s,
        // which would send the browser to log in again over a session that still stands (change
        // request 4.2).
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, Orders(listener));
        gateway.Tokens.Result = new AccessTokenResult.ProviderUnavailable();
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("idp_unavailable", await Problem.CodeOf(response));
        Assert.Empty(orders.Requests);
    }

    [Theory]
    [InlineData(nameof(AccessTokenResult.Unauthenticated), HttpStatusCode.Unauthorized)]
    [InlineData(nameof(AccessTokenResult.SessionEnded), HttpStatusCode.Unauthorized)]
    [InlineData(nameof(AccessTokenResult.ProviderUnavailable), HttpStatusCode.ServiceUnavailable)]
    public async Task A_call_the_token_step_answers_leaves_no_rule_id(string outcome, HttpStatusCode status)
    {
        // contracts/telemetry.md: the 401 for a session that yields no token is not a rejection, so it
        // leaves rule null, and change request 4.2's decision says the same of the 503. Fails if the step
        // borrows auth.session, which is the shell gate's.
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, new ProxyHostOptions { Telemetry = true }, TestApps.Entry("app1", apis: [TestApps.Api("orders")]));
        gateway.Tokens.Result = outcome switch
        {
            nameof(AccessTokenResult.Unauthenticated) => new AccessTokenResult.Unauthenticated(),
            nameof(AccessTokenResult.SessionEnded) => new AccessTokenResult.SessionEnded(),
            nameof(AccessTokenResult.ProviderUnavailable) => new AccessTokenResult.ProviderUnavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not an outcome this test arranges"),
        };
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Null(Assert.Single(gateway.TelemetryRules));
    }

    [Fact]
    public async Task The_upstream_receives_this_apps_session_token_as_a_bearer_token()
    {
        // N-11's "it sees the session's token". Two apps with two tokens, so a token taken from the
        // wrong app, or a constant, fails.
        EchoUpstream upstream = new();
        await using EchoUpstreamListener listener = await upstream.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]),
            TestApps.Entry("app2", apis: [TestApps.Api("reports", listener.BaseAddress.AbsoluteUri)]));
        gateway.Tokens.ForApp("app1", "token-of-app1");
        gateway.Tokens.ForApp("app2", "token-of-app2");
        using HttpClient client = new();

        using HttpResponseMessage first = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app2.spa-poc.test", "/api/reports/items"), Ct);
        using HttpResponseMessage second = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(["Bearer token-of-app2"], upstream.Requests[0].Headers["Authorization"]);
        Assert.Equal(["Bearer token-of-app1"], upstream.Requests[1].Headers["Authorization"]);

        // Once per call, ahead of the forwarder.
        Assert.Equal(["app2", "app1"], gateway.Tokens.Calls);
    }

    [Fact]
    public async Task With_no_token_source_registered_the_call_is_401_unauthenticated_and_reaches_no_upstream()
    {
        // Fails closed (plan row 4.2: "answer 401 when there is none"). With no source to ask, no call
        // has a token, and one that went on would go upstream bare. It is answered as a call with no
        // session is, with no rule id, and the browser's own Authorization gets it no further.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, new ProxyHostOptions { TokenSource = false, Telemetry = true }, Orders(listener));
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpRequestMessage request = gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer from-the-browser");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("unauthenticated", await Problem.CodeOf(response));
        Assert.Empty(orders.Requests);
        Assert.Null(Assert.Single(gateway.TelemetryRules));
    }

    private static AppDefinition Orders(EchoUpstreamListener listener) =>
        TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]);
}
