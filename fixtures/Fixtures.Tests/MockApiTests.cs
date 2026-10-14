using System.Net;
using System.Text.Json;
using MockApi;

namespace Fixtures.Tests;

/// <summary>
/// The mock API, task 5.3. It stands for an upstream that validates the bearer token itself, which is
/// assumption A5 and criterion 7: the gateway is not the only check. So these tests call the listener
/// directly, with no gateway anywhere, and that is the point of them.
/// </summary>
public sealed class MockApiTests
{
    [Fact]
    public async Task Whoami_reports_the_client_the_scopes_and_the_bound_audience()
    {
        // F-08: "/whoami reports the right client and scopes on each listener, and the bound audience
        // on orders."
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await MockApiHarness.BodyOf(response);
        Assert.Equal("orders", body.GetProperty("api").GetString());
        Assert.Equal(IdpHarness.Client1, body.GetProperty("clientId").GetString());
        Assert.Contains(MockApiHarness.OrdersRead, body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(MockApiHarness.Orders, body.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task The_reports_listener_reports_no_audience_because_its_app_binds_none()
    {
        // Port 5081 does not check aud, because app 2 has no resource binding. The absence is the
        // fixture's, not an oversight: app 2's definition passes no resource at all.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("reports", "/whoami", await harness.App2TokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await MockApiHarness.BodyOf(response);
        Assert.Equal("reports", body.GetProperty("api").GetString());
        Assert.Equal(IdpHarness.Client2, body.GetProperty("clientId").GetString());
        Assert.Null(body.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Echo_returns_what_arrived()
    {
        // The deployed negative tests read this to check the gateway stripped what the upstream
        // contract forbids, so what it reports has to be what actually arrived.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("orders", "/echo?page=2", await harness.App1TokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await MockApiHarness.BodyOf(response);
        Assert.Equal("/echo", body.GetProperty("path").GetString());
        Assert.Equal("?page=2", body.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Admin_is_refused_for_scope_on_both_listeners()
    {
        // F-08: "/admin returns 403." The scope exists on the resource server and no app asks for
        // it, so the refusal is about scope rather than about a route that is not there.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage orders = await harness.CallAsync("orders", "/admin", await harness.App1TokenAsync());
        HttpResponseMessage reports = await harness.CallAsync("reports", "/admin", await harness.App2TokenAsync());

        Assert.Equal(HttpStatusCode.Forbidden, orders.StatusCode);
        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(orders)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, reports.StatusCode);
        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(reports)).GetProperty("error").GetString());
    }

    // ---- N-17: the second upstream each app lists -------------------------------------------------

    [Fact]
    public async Task App_ones_token_at_the_reports_api_is_refused_as_an_unadmitted_client()
    {
        // N-17, half one: "Send app 1's token to the reports API ... The mock API refuses both: wrong
        // client". App 1 lists reports as a second upstream, so the gateway forwards this happily;
        // the refusal is the API owner's, which is criterion 7 at work.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("reports", "/whoami", await harness.App1TokenAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("client_not_admitted", (await MockApiHarness.BodyOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task App_twos_token_at_the_orders_api_is_refused_for_its_missing_audience()
    {
        // N-17, half two: "and on orders a missing audience". App 2's token carries no aud at all,
        // because its definition passes no resource, and orders binds one. The audience check runs
        // in the handler, so it answers before admission does and the refusal is a 401.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", await harness.App2TokenAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- each kind of wrong token, in turn --------------------------------------------------------

    [Fact]
    public async Task No_token_is_refused()
    {
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.CallAsync("orders", "/whoami", token: null)).StatusCode);
    }

    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_refused()
    {
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.Faults.IssuerOverride = "https://not-the-issuer.example";

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync())).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_an_unpublished_key_is_refused()
    {
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.Faults.SignWithUnpublishedKey = true;

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync())).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        // The provider issues an already-expired token rather than a test waiting for one, and the
        // listener runs with no clock skew, or the library's five-minute default would accept it.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-5);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync())).StatusCode);
    }

    [Fact]
    public async Task An_id_token_presented_as_a_bearer_token_is_refused()
    {
        // token_use is the only check that separates the two: an ID token carries the same issuer
        // and the same signature.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.Faults.AccessTokenUse = "id";

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync())).StatusCode);
    }

    [Fact]
    public async Task A_token_that_should_have_been_bound_but_is_not_is_refused_by_orders()
    {
        // The provider accepting resource and ignoring it, which is the fault switch written for
        // exactly this check.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.Faults.IgnoreResource = true;

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await harness.CallAsync("orders", "/whoami", await harness.App1TokenAsync())).StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_read_scope_is_refused_for_scope()
    {
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        string token = await harness.App1TokenAsync(scope: "openid profile");

        HttpResponseMessage response = await harness.CallAsync("orders", "/whoami", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(response)).GetProperty("error").GetString());
    }

    // ---- F-07: admission is the API owner's decision, taken at run time --------------------------

    [Fact]
    public async Task A_client_admitted_while_the_api_runs_is_served_without_a_restart()
    {
        // F-07: the mock API refuses app 3's call because it has not admitted that client, "and in
        // the hermetic tier the harness then admits client 3 and the call succeeds". A restart
        // between the two halves would prove the wrong thing, because F-07 is about nothing
        // restarting.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();
        harness.Idp.Idp.AddClient("client-3", "secret-3", "https://app3.spa-poc.test:8443/signin-oidc",
            "https://app3.spa-poc.test:8443/");

        Login app3 = await harness.Idp.LoginAsync("client-3", "secret-3",
            "https://app3.spa-poc.test:8443/signin-oidc", resource: null,
            scope: "openid profile " + MockApiHarness.ReportsRead);

        HttpResponseMessage refused = await harness.CallAsync("reports", "/whoami", app3.AccessToken);
        harness.Api.AdmissionOf("reports").Admit("client-3");
        HttpResponseMessage served = await harness.CallAsync("reports", "/whoami", app3.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("client_not_admitted", (await MockApiHarness.BodyOf(refused)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task The_default_listeners_are_the_ports_the_app_definitions_name()
    {
        // fixtures/apps/app1.json and app2.json name http://127.0.0.1:5080 and :5081 as their
        // upstreams. The harness lets the system pick a port, so nothing else would notice if these
        // two drifted apart.
        MockApiOptions options = MockApiOptions.Default("https://issuer.example", "https://issuer.example/x", "c1", "c2");

        Assert.Equal([5080, 5081], options.Listeners.Select(listener => listener.Port));
        Assert.Equal(["orders", "reports"], options.Listeners.Select(listener => listener.Name));
        Assert.Equal(MockApiHarness.Orders, options.Listeners[0].Audience);
        Assert.Null(options.Listeners[1].Audience);
    }
}
