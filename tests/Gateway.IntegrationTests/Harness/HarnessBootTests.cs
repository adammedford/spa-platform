using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using TestIdp;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// Task 6.1, "Done when: a stub gateway boots in the harness". The gateway is the shipped composition,
/// which requires a session as ADR-0008 decides (<c>AddPlatformIdentity</c> binds the token source and
/// <c>UsePlatformShellGate</c> is always in the pipeline), and no browser here signs in: every
/// assertion is about a stage that runs before the shell gate in contracts/pipeline.md, or about the
/// harness's own parts. Where a request does reach the shell gate, the test reads the answer a
/// navigation without a session gets there, the redirect to the provider, and nothing beyond it.
/// </summary>
public sealed class HarnessBootTests(HarnessBootTests.Cell cell) : IClassFixture<HarnessBootTests.Cell>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One harness for the read-only tests in this class.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Harness = await GatewayHarness.StartAsync();

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    [Fact]
    public async Task The_gateway_is_live_and_ready_on_the_first_registry_revision()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        BrowserResponse health = await browser.NavigateAsync("https://anything.example/healthz", ct: Ct);
        BrowserResponse ready = await browser.NavigateAsync("https://anything.example/readyz", ct: Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using JsonDocument body = JsonDocument.Parse(ready.Body);
        Assert.Equal(1, body.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void The_gateway_reads_its_own_settings_file_under_the_harness_configuration()
    {
        IConfiguration configuration = Harness.Services.GetRequiredService<IConfiguration>();

        string settings = Path.Combine(FixtureFiles.RepositoryRoot, "src", "Gateway", "appsettings.json");
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(settings));
        string expected = source.RootElement.GetProperty("Logging").GetProperty("LogLevel")
            .GetProperty("Microsoft.AspNetCore").GetString()!;
        Assert.Equal(expected, configuration["Logging:LogLevel:Microsoft.AspNetCore"]);
        Assert.Equal(Harness.Idp.BaseUrl, configuration[$"Issuers:{Harness.Options.IssuerName}:Authority"]);
    }

    [Fact]
    public void The_registry_holds_each_fixture_app_with_its_client_and_its_upstreams_on_the_harness_listeners()
    {
        RegistrySnapshot snapshot = Harness.Registry.Current;

        Assert.Equal(["app1", "app2"], snapshot.ByAppId.Keys.Order());
        Assert.Equal(FixtureFiles.AppDefinition("app1")["host"]!.GetValue<string>(), snapshot.ByAppId["app1"].Host);
        Assert.Equal(GatewayHarness.ClientIdFor("app1"), snapshot.ByAppId["app1"].OAuth.ClientId);

        Dictionary<string, Uri> apis = snapshot.ByAppId["app1"].Apis.ToDictionary(api => api.Name, api => api.Upstream);
        Assert.Equal(Harness.MockApi.BaseAddressOf("orders").GetLeftPart(UriPartial.Authority), apis["orders"].GetLeftPart(UriPartial.Authority));
        Assert.Equal(Harness.MockApi.BaseAddressOf("reports").GetLeftPart(UriPartial.Authority), apis["reports"].GetLeftPart(UriPartial.Authority));
        Assert.Equal(Harness.EchoAddress.GetLeftPart(UriPartial.Authority), apis["echo"].GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public async Task The_harness_registry_echo_address_reaches_its_echo_listener()
    {
        Uri upstream = Harness.Registry.Current.ByAppId["app1"].Apis.Single(api => api.Name == "echo").Upstream;
        using TestBrowser browser = Harness.CreateBrowser();
        BrowserResponse response = await browser.NavigateAsync(new Uri(upstream, "/harness-echo?probe=6.1"), ct: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Harness.Echo.Requests, request => request.Path == "/harness-echo" && request.Query == "?probe=6.1");
    }

    [Fact]
    public async Task A_host_the_registry_names_resolves_and_any_other_host_gets_404_before_routing()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        BrowserResponse known = await browser.NavigateAsync(Harness.App("app1").Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        BrowserResponse unknown = await browser.NavigateAsync("https://nope.spa-poc.test:8443/", ct: Ct);
        BrowserResponse wrongPort = await browser.NavigateAsync("https://app1.spa-poc.test:9443/", ct: Ct);

        // UsePlatformSecurityHeaders writes the app's content security policy on every response for a
        // resolved host, so its presence says host resolution resolved the app, whatever the stages after it answer.
        Assert.Contains("frame-ancestors 'none'", known.Header("Content-Security-Policy"));

        // What answers it here is the shell gate: the browser has no session, so it is sent to the provider.
        Assert.Equal(HttpStatusCode.Redirect, known.StatusCode);
        Assert.StartsWith(Harness.Idp.AuthorizeEndpoint + "?", known.Header("Location"), StringComparison.Ordinal);

        Contract.ErrorRow unknownHost = Contract.Error("not_found");
        Assert.Equal(unknownHost.Status, (int)unknown.StatusCode);
        Assert.Equal(unknownHost.Status, (int)wrongPort.StatusCode);
    }

    [Fact]
    public async Task A_raw_socket_reaches_the_gateway_with_the_host_header_it_was_given()
    {
        RawResponse unknown = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", "/", "evil.example", ct: Ct);
        RawResponse health = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", "/healthz", "evil.example", ct: Ct);

        Assert.Equal(Contract.Error("not_found").Status, unknown.StatusCode);
        Assert.Equal(200, health.StatusCode);
    }

    [Fact]
    public async Task The_request_gate_reads_the_fetch_metadata_the_browser_computes()
    {
        using TestBrowser browser = Harness.CreateBrowser();

        // A page on app 2 with no login: /healthz answers on any host and sets nothing, where a
        // navigation to the shell would be sent to the provider.
        await browser.NavigateAsync(Harness.App("app2").Url("/healthz"), ct: Ct);

        // From app 2's page, app 1's API is same-site: the value a sibling SPA produces, which the gate
        // refuses before authentication runs (contracts/request-gate.md).
        BrowserResponse sibling = await browser.GetAsync(Harness.App("app1").Url("/api/orders/whoami").ToString(), ct: Ct);

        Contract.ErrorRow refused = Contract.Error("origin_rejected");
        Assert.Equal(FetchMetadata.SameSite, sibling.Sent("Sec-Fetch-Site"));
        Assert.Equal(refused.Status, (int)sibling.StatusCode);
        Assert.Contains(refused.Code, sibling.Body);
    }

    [Fact]
    public async Task The_mock_api_validates_the_test_providers_tokens_and_admits_each_apps_own_client()
    {
        HarnessApp app1 = Harness.App("app1");
        string token = await AccessTokenAsync(app1, "openid https://orders.spa-poc.test/read", "https://orders.spa-poc.test");

        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        using HttpRequestMessage whoami = new(HttpMethod.Get, new Uri(Harness.MockApi.BaseAddressOf("orders"), "/whoami"));
        whoami.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage orders = await client.SendAsync(whoami, Ct);

        using HttpRequestMessage elsewhere = new(HttpMethod.Get, new Uri(Harness.MockApi.BaseAddressOf("reports"), "/whoami"));
        elsewhere.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage reports = await client.SendAsync(elsewhere, Ct);

        Assert.Equal(HttpStatusCode.OK, orders.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await orders.Content.ReadAsStringAsync(Ct));
        Assert.Equal(app1.ClientId, body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("https://orders.spa-poc.test", body.RootElement.GetProperty("audience").GetString());

        // reports admits app 2's client only, as in the cell.
        Assert.Equal(HttpStatusCode.Forbidden, reports.StatusCode);
        Assert.Contains("client_not_admitted", await reports.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_gateways_log_lines_are_captured_as_its_formatter_writes_them()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await browser.NavigateAsync(Harness.App("app2").Url("/healthz"), ct: Ct);

        // A navigation with no session, which the shell gate sends to the provider; its request line is
        // still app 2's.
        BrowserResponse challenged = await browser.NavigateAsync(Harness.App("app2").Url("/"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);

        // UsePlatformTelemetry logs after the downstream response returns (contracts/telemetry.md), so the
        // client receiving the body does not prove the request line has been captured yet.
        static bool IsAppRequest(JsonElement line) =>
            line.TryGetProperty("app_id", out JsonElement app) && app.GetString() == "app2"
            && line.TryGetProperty("registry_rev", out JsonElement revision) && revision.GetInt64() == 1;

        for (int attempt = 0; attempt < 100 && !Harness.Log.Json.Any(IsAppRequest); attempt++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Contains(Harness.Log.Json, IsAppRequest);
    }

    /// <summary>
    /// An access token for <paramref name="app"/>, by the authorization code grant with PKCE, as the
    /// gateway will get one once workstream 3 lands. The browser carries the front channel and stops at the
    /// redirect back, because the callback is the gateway's and is not what this test is about.
    /// </summary>
    private async Task<string> AccessTokenAsync(HarnessApp app, string scope, string resource)
    {
        string verifier = Pkce.NewVerifier();
        Uri redirect = app.Url("/signin-oidc");
        string authorize = Harness.Idp.AuthorizeEndpoint + "?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = redirect.ToString(),
            ["scope"] = scope,
            ["resource"] = resource,
            ["code_challenge"] = Pkce.Challenge(verifier),
            ["code_challenge_method"] = "S256",
        }.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

        using TestBrowser browser = Harness.CreateBrowser();
        BrowserResponse back = await browser.NavigateAsync(authorize, new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        string code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(back.Header("Location")!).Query)["code"].ToString();

        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        using HttpResponseMessage response = await client.PostAsync(Harness.Idp.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect.ToString(),
            ["code_verifier"] = verifier,
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret,
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return tokens.RootElement.GetProperty("access_token").GetString()!;
    }
}
