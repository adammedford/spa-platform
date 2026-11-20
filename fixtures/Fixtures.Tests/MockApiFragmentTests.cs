using System.Collections.Immutable;
using System.Net;
using MockApi;

namespace Fixtures.Tests;

/// <summary>
/// The mock API serving one upstream to a shell's and a fragment's tokens (#998, part of #961):
/// "the cross-client refusal is proved by client_id, 403 client_not_admitted ...; an audience check
/// proves resource binding only". So a listener checks a set of audiences, and admits clients per
/// route. Called directly, with no gateway, as the rest of the mock API's tests are.
/// </summary>
public sealed class MockApiFragmentTests
{
    /// <summary>A fragment's own client, as the apps root creates one per (appId, fragmentId).</summary>
    private const string FragmentClient = "client-shell-orders";

    private const string FragmentSecret = "secret-shell-orders";

    private const string FragmentRedirect = "https://shell.spa-poc.test:8443/signin-oidc/orders";

    private const string Elsewhere = "https://elsewhere.spa-poc.test";

    [Theory]
    [InlineData(MockApiHarness.Orders)]
    [InlineData(MockApiHarness.Reports)]
    public async Task A_token_bound_to_any_of_a_listeners_audiences_is_accepted(string resource)
    {
        // A fragment may set its own resource, so one upstream can see two audiences.
        // Mutation: validating against the first of ValidAudiences only turns the Reports case red.
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
            [Listener(audience: null, audiences: [MockApiHarness.Orders, MockApiHarness.Reports], admitted: [IdpHarness.Client1])]);

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", await TokenAsync(harness, IdpHarness.Client1, resource));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(resource, (await MockApiHarness.BodyOf(response)).GetProperty("audience").GetString());
    }

    [Theory]
    [InlineData(Elsewhere)]
    [InlineData(null)]
    public async Task A_token_bound_to_none_of_a_listeners_audiences_is_refused(string? resource)
    {
        // A present and wrong aud, and no aud at all. Mutation: turning ValidateAudience off when
        // Audience is null (the pre-#998 rule) turns both red.
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
            [Listener(audience: null, audiences: [MockApiHarness.Orders, MockApiHarness.Reports], admitted: [IdpHarness.Client1])]);

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", await TokenAsync(harness, IdpHarness.Client1, resource));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Audience_is_always_one_of_a_listeners_audiences()
    {
        // Audience and ValidAudiences together: the options a listener had before #998 keep their
        // meaning when the set is added beside them. Mutation: ValidAudiences replacing Audience
        // instead of joining it turns the Orders call red.
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
            [Listener(audience: MockApiHarness.Orders, audiences: [MockApiHarness.Reports], admitted: [IdpHarness.Client1])]);

        HttpResponseMessage orders = await harness.CallAsync("orders", "/whoami", await TokenAsync(harness, IdpHarness.Client1, MockApiHarness.Orders));
        HttpResponseMessage reports = await harness.CallAsync("orders", "/whoami", await TokenAsync(harness, IdpHarness.Client1, MockApiHarness.Reports));

        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
    }

    [Fact]
    public async Task A_listener_with_no_audience_still_checks_none()
    {
        // "no audience still means no audience check, as for reports today". Mutation: always
        // validating the audience turns this red, since the token has no aud.
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
            [Listener(audience: null, audiences: [], admitted: [IdpHarness.Client1])]);

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", await TokenAsync(harness, IdpHarness.Client1, resource: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task One_listener_admits_the_shells_client_on_one_route_and_a_fragments_on_another()
    {
        // The upstream receives the same path from a shell's route and a fragment's and tells the
        // two apart only by client_id (contracts/upstream-contract.md -> "Fragment routes"), so the
        // routes here are the API's own. Mutation: Guard checking the listener's list and not the
        // route's turns all four red (the listener admits nobody).
        await using MockApiHarness harness = await MockApiHarness.StartAsync([ShellAndFragmentListener()]);
        string shell = await TokenAsync(harness, IdpHarness.Client1, MockApiHarness.Orders);
        string fragment = await TokenAsync(harness, FragmentClient, MockApiHarness.Orders);

        HttpResponseMessage shellOnWhoami = await harness.CallAsync("orders", "/whoami", shell);
        HttpResponseMessage fragmentOnEcho = await harness.CallAsync("orders", "/echo", fragment);
        HttpResponseMessage shellOnEcho = await harness.CallAsync("orders", "/echo", shell);
        HttpResponseMessage fragmentOnWhoami = await harness.CallAsync("orders", "/whoami", fragment);

        Assert.Equal(HttpStatusCode.OK, shellOnWhoami.StatusCode);
        Assert.Equal(IdpHarness.Client1, (await MockApiHarness.BodyOf(shellOnWhoami)).GetProperty("clientId").GetString());
        Assert.Equal(HttpStatusCode.OK, fragmentOnEcho.StatusCode);
        await AssertNotAdmitted(shellOnEcho);
        await AssertNotAdmitted(fragmentOnWhoami);
    }

    [Fact]
    public async Task A_client_not_admitted_on_the_route_is_refused_before_its_scope_is_judged()
    {
        // A token both unadmitted on /whoami and without the read scope answers client_not_admitted,
        // and the same token where it is admitted answers insufficient_scope, so the order is seen
        // from both sides. Mutation: moving the scope check before admission turns the first red.
        await using MockApiHarness harness = await MockApiHarness.StartAsync([ShellAndFragmentListener()]);
        string unscoped = await TokenAsync(harness, FragmentClient, MockApiHarness.Orders, scope: "openid profile");

        HttpResponseMessage whoami = await harness.CallAsync("orders", "/whoami", unscoped);
        HttpResponseMessage echo = await harness.CallAsync("orders", "/echo", unscoped);

        await AssertNotAdmitted(whoami);
        Assert.Equal(HttpStatusCode.Forbidden, echo.StatusCode);
        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(echo)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_route_with_no_admission_of_its_own_admits_the_listeners_clients()
    {
        // /admin is in no route's list, so the listener's AdmittedClients answer for it: the shell's
        // client reaches the scope decision, the fragment's does not. Mutation: refusing every
        // route absent from RouteAdmissions turns the shell's call red (client_not_admitted, not
        // insufficient_scope).
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
            [ShellAndFragmentListener() with { AdmittedClients = [IdpHarness.Client1] }]);

        HttpResponseMessage shell = await harness.CallAsync("orders", "/admin", await TokenAsync(harness, IdpHarness.Client1, MockApiHarness.Orders));
        HttpResponseMessage fragment = await harness.CallAsync("orders", "/admin", await TokenAsync(harness, FragmentClient, MockApiHarness.Orders));

        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(shell)).GetProperty("error").GetString());
        await AssertNotAdmitted(fragment);
    }

    [Fact]
    public async Task A_client_admitted_on_a_route_while_the_api_runs_is_served_there_without_a_restart()
    {
        // F-07's run-time admission, on one route. Mutation: Admit(client, route) adding to the
        // listener's list instead of the route's turns the second call red.
        await using MockApiHarness harness = await MockApiHarness.StartAsync([ShellAndFragmentListener()]);
        string fragment = await TokenAsync(harness, FragmentClient, MockApiHarness.Orders);

        HttpResponseMessage refused = await harness.CallAsync("orders", "/whoami", fragment);
        harness.Api.AdmissionOf("orders").Admit(FragmentClient, "/whoami");
        HttpResponseMessage served = await harness.CallAsync("orders", "/whoami", fragment);

        await AssertNotAdmitted(refused);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(FragmentClient, (await MockApiHarness.BodyOf(served)).GetProperty("clientId").GetString());
    }

    [Fact]
    public void The_default_listeners_keep_one_audience_and_no_route_admission()
    {
        // MockApiOptions.Default is what the composed harness and the deployed configuration call,
        // so the additions leave it as it was: orders checks its one audience, reports none, and no
        // route has an admission of its own. Mutation: defaulting ValidAudiences to empty while
        // Audience is set turns the orders half red.
        MockApiOptions options = MockApiOptions.Default("https://issuer.example", "https://issuer.example/x", "c1", "c2");

        Assert.Equal([MockApiHarness.Orders], options.Listeners[0].ValidAudiences);
        Assert.Empty(options.Listeners[1].ValidAudiences);
        Assert.All(options.Listeners, listener => Assert.Empty(listener.RouteAdmissions));
    }

    /// <summary>
    /// orders, admitting nobody at the listener: the shell's client (client 1) on /whoami, the
    /// fragment's on /echo.
    /// </summary>
    private static ApiListenerOptions ShellAndFragmentListener() =>
        Listener(audience: MockApiHarness.Orders, audiences: [], admitted: []) with
        {
            RouteAdmissions = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("/whoami", [IdpHarness.Client1])
                .Add("/echo", [FragmentClient]),
        };

    private static ApiListenerOptions Listener(string? audience, ImmutableArray<string> audiences, ImmutableArray<string> admitted) =>
        new("orders", Port: 0, ScopeIdentifier: MockApiHarness.Orders, Audience: audience, AdmittedClients: admitted)
        {
            ValidAudiences = audiences,
        };

    /// <summary>
    /// An access token for client 1 or for the fragment's client, bound to <paramref name="resource"/>,
    /// with the orders read scope unless the test names another.
    /// </summary>
    private static async Task<string> TokenAsync(
        MockApiHarness harness, string client, string? resource, string scope = "openid profile " + MockApiHarness.OrdersRead)
    {
        if (client == FragmentClient)
        {
            harness.Idp.Idp.AddClient(FragmentClient, FragmentSecret, FragmentRedirect, "https://shell.spa-poc.test:8443/");
            return (await harness.Idp.LoginAsync(FragmentClient, FragmentSecret, FragmentRedirect, resource: resource, scope: scope)).AccessToken;
        }

        return (await harness.Idp.LoginAsync(IdpHarness.Client1, IdpHarness.Secret1, IdpHarness.Redirect1, resource: resource, scope: scope)).AccessToken;
    }

    private static async Task AssertNotAdmitted(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("client_not_admitted", (await MockApiHarness.BodyOf(response)).GetProperty("error").GetString());
    }
}
