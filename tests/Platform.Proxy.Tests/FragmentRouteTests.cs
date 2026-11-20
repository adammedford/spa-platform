using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// A fragment's API routes, over a socket at each end (#996, part of the fragments spec #961):
/// <c>/fragments/{fragmentId}/api/{name}/{rest}</c> forwards <c>{rest}</c> as a shell route does, with
/// the fragment's token and never the shell's; the token step's cross-check against
/// <see cref="IFragmentFeature"/> fails closed; <c>FragmentLoginRequired</c> is answered; and the
/// catch-all answers <c>api.unknown</c> under a fragment the app lists and nothing under one it does not.
/// contracts/upstream-contract.md -> Fragment routes and contracts/pipeline.md -> Which token set a
/// request uses are the contracts.
/// </summary>
public sealed class FragmentRouteTests
{
    private const string Host = "app1.spa-poc.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fragment_api_route_removes_its_prefix_and_forwards_the_rest_unchanged()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, ShellWithFragment(listener));
        gateway.Tokens.ForFragment("app1", "frag1", "token-of-frag1");
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, Host, "/fragments/frag1/api/orders/items/7?page=2"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EchoedRequest received = Assert.Single(orders.Requests);
        Assert.Equal("/items/7", received.Path);
        Assert.Equal("?page=2", received.Query);
    }

    [Fact]
    public async Task O11_the_shell_route_and_the_fragment_route_carry_their_own_clients_token()
    {
        // O11's component half: one upstream, named "orders" in the shell's apis[] and the fragment's.
        // Each token is arranged to carry its own client's client_id, the per-client claim the mock
        // API's client_not_admitted refusal reads (workstreams 5 and 6 prove that refusal), so the
        // upstream double reads which client each call's token was issued to.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        AppDefinition app = ShellWithFragment(listener);
        FragmentDefinition fragment = Assert.Single(app.Fragments);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, app);
        gateway.Tokens.ForApp("app1", TokenFor(app.OAuth.ClientId));
        gateway.Tokens.ForFragment("app1", "frag1", TokenFor(fragment.OAuth.ClientId));
        using HttpClient client = new();

        using HttpResponseMessage shell = await client.SendAsync(
            gateway.Request(HttpMethod.Get, Host, "/api/orders/items"), Ct);
        using HttpResponseMessage composed = await client.SendAsync(
            gateway.Request(HttpMethod.Get, Host, "/fragments/frag1/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(HttpStatusCode.OK, composed.StatusCode);
        Assert.Equal(2, orders.Requests.Count);
        Assert.Equal("client-app1", ClientIdOf(orders.Requests[0]));
        Assert.Equal("client-app1-frag1", ClientIdOf(orders.Requests[1]));
        Assert.Equal([null, "frag1"], gateway.Tokens.FragmentCalls);
    }

    [Fact]
    public async Task A_fragment_route_with_no_fragment_pinned_forwards_nothing_and_asks_for_no_token()
    {
        // The swap race contracts/pipeline.md names: the route follows the live snapshot, the request
        // the one it was resolved against. Answered as an unknown fragmentId is, 404 not_found, no rule.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, new ProxyHostOptions { Telemetry = true, FragmentPin = (_, _) => null }, ShellWithFragment(listener));
        gateway.Tokens.Result = new AccessTokenResult.Token("token-of-the-shell");
        gateway.Tokens.FragmentResult = new AccessTokenResult.Token("token-of-a-fragment");

        await AssertRefusedClosedAsync(gateway, orders, "/fragments/frag1/api/orders/items");
    }

    [Fact]
    public async Task A_fragment_route_with_another_fragment_pinned_forwards_nothing()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        AppDefinition app = TestApps.Entry("app1", fragments: [
            TestApps.Fragment("frag1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]),
            TestApps.Fragment("frag2", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]),
        ]);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            new ProxyHostOptions { Telemetry = true, FragmentPin = (a, _) => SettableFragmentFeature.For(a, "frag2") },
            app);
        gateway.Tokens.FragmentResult = new AccessTokenResult.Token("token-of-a-fragment");

        await AssertRefusedClosedAsync(gateway, orders, "/fragments/frag1/api/orders/items");
    }

    [Fact]
    public async Task A_shell_route_with_a_fragment_pinned_forwards_nothing()
    {
        // The other direction: a fragment's set is never sent on the shell's route.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            new ProxyHostOptions { Telemetry = true, FragmentPin = (a, _) => SettableFragmentFeature.For(a, "frag1") },
            ShellWithFragment(listener));
        gateway.Tokens.FragmentResult = new AccessTokenResult.Token("token-of-a-fragment");

        await AssertRefusedClosedAsync(gateway, orders, "/api/orders/items");
    }

    [Fact]
    public async Task A_fragment_route_with_another_apps_entry_pinned_forwards_nothing()
    {
        // "The same fragment of the same app": a pin of another app's fragment of the same id fails too.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        AppDefinition other = TestApps.Entry("app2", fragments: [TestApps.Fragment("frag1", appId: "app2")]);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            new ProxyHostOptions
            {
                Telemetry = true,
                AppPin = _ => other,
                FragmentPin = (_, _) => SettableFragmentFeature.For(other, "frag1"),
            },
            ShellWithFragment(listener));
        gateway.Tokens.FragmentResult = new AccessTokenResult.Token("token-of-a-fragment");

        await AssertRefusedClosedAsync(gateway, orders, "/fragments/frag1/api/orders/items");
    }

    [Fact]
    public async Task FragmentLoginRequired_answers_401_fragment_login_required_naming_no_fragment_and_no_rule()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, new ProxyHostOptions { Telemetry = true }, ShellWithFragment(listener));
        gateway.Tokens.ForFragment("app1", "frag1", new AccessTokenResult.FragmentLoginRequired());
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true });

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Post, Host, "/fragments/frag1/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["code", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(401, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("fragment_login_required", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(orders.Requests);
        Assert.Null(Assert.Single(gateway.TelemetryRules));
    }

    [Fact]
    public async Task An_api_name_the_fragment_does_not_list_answers_api_unknown_and_no_upstream_sees_it()
    {
        // The shell lists "reports"; the fragment does not. The namespaces are separate, so the shell's
        // name is no route under the fragment.
        EchoUpstream upstream = new();
        await using EchoUpstreamListener listener = await upstream.ListenAsync(Ct);
        AppDefinition app = TestApps.Entry(
            "app1",
            apis: [TestApps.Api("reports", listener.BaseAddress.AbsoluteUri)],
            fragments: [TestApps.Fragment("frag1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)])]);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, new ProxyHostOptions { Telemetry = true }, app);
        gateway.Tokens.FragmentResult = new AccessTokenResult.Token("token-of-a-fragment");
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, Host, "/fragments/frag1/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown_api", await Problem.CodeOf(response));
        Assert.Equal([RuleIds.ApiUnknown], gateway.TelemetryRules);
        Assert.Empty(upstream.Requests);
        Assert.Empty(gateway.Tokens.Calls);
    }

    [Theory]
    [InlineData("/fragments/nope/api/orders/items")]
    [InlineData("/fragments/frag1/api/orders/items")]
    public async Task A_fragment_api_path_for_a_fragment_the_app_does_not_list_is_not_routed(string path)
    {
        // Not routed: the catch-all matches only a fragment host resolution pinned, so the path falls
        // to what follows the proxy (hosting's asset endpoint in the composed gateway; nothing here, so
        // routing's empty 404). The second row is a host whose app lists no fragments at all.
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct,
            new ProxyHostOptions { Telemetry = true },
            path.Contains("nope", StringComparison.Ordinal)
                ? TestApps.Entry("app1", fragments: [TestApps.Fragment("frag1", apis: [TestApps.Api("orders")])])
                : TestApps.Entry("app1", apis: [TestApps.Api("orders")]));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(gateway.Request(HttpMethod.Get, Host, path), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(Assert.Single(gateway.TelemetryRules));
        Assert.Empty(gateway.Tokens.Calls);
    }

    private static async Task AssertRefusedClosedAsync(ProxyHost gateway, EchoUpstream upstream, string path)
    {
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(gateway.Request(HttpMethod.Get, Host, path), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await Problem.CodeOf(response));
        Assert.Empty(upstream.Requests);
        Assert.Empty(gateway.Tokens.Calls);
        Assert.Null(Assert.Single(gateway.TelemetryRules));
    }

    private static AppDefinition ShellWithFragment(EchoUpstreamListener listener) =>
        TestApps.Entry(
            "app1",
            apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)],
            fragments: [TestApps.Fragment("frag1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)])]);

    /// <summary>
    /// An unsigned token shaped as a JWT whose payload names <paramref name="clientId"/>: what the fake
    /// token source hands the proxy in place of a provider-issued token. The proxy treats a token as
    /// opaque, so the shape matters only to the upstream double reading it back.
    /// </summary>
    private static string TokenFor(string clientId) =>
        Base64Url("{\"alg\":\"none\"}") + "." + Base64Url(JsonSerializer.Serialize(new { client_id = clientId })) + ".";

    private static string ClientIdOf(EchoedRequest request)
    {
        string authorization = Assert.Single(request.Headers["Authorization"]);
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);
        string payload = authorization["Bearer ".Length..].Split('.')[1];
        string padded = payload.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (payload.Length % 4)) % 4);
        using JsonDocument claims = JsonDocument.Parse(Convert.FromBase64String(padded));
        return claims.RootElement.GetProperty("client_id").GetString()!;
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
