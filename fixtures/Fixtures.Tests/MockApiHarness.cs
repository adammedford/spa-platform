using System.Net.Http.Headers;
using System.Text.Json;
using MockApi;

namespace Fixtures.Tests;

/// <summary>
/// A started test identity provider and a started mock API that trusts it, which is what every check
/// of task 5.3 needs. The two listeners are the plan's: <c>orders</c> admits app 1's client and binds
/// the audience, <c>reports</c> admits app 2's and does not.
///
/// The ports are operating-system assigned here. The fixtures name 5080 and 5081, and
/// <see cref="MockApiOptions.Default"/> carries those, but a test that bound them could not run twice
/// at once.
/// </summary>
internal sealed class MockApiHarness : IAsyncDisposable
{
    public const string Orders = "https://orders.spa-poc.test";
    public const string Reports = "https://reports.spa-poc.test";
    public const string OrdersRead = Orders + "/read";
    public const string ReportsRead = Reports + "/read";

    private MockApiHarness(IdpHarness idp, MockApiHost api, HttpClient http)
    {
        Idp = idp;
        Api = api;
        Http = http;
    }

    public IdpHarness Idp { get; }

    public MockApiHost Api { get; }

    public HttpClient Http { get; }

    public static async Task<MockApiHarness> StartAsync()
    {
        IdpHarness idp = await IdpHarness.StartAsync();

        MockApiHost api = await MockApiHost.StartAsync(new MockApiOptions(
            Issuer: idp.Idp.BaseUrl,
            MetadataAddress: idp.Idp.DiscoveryUri,
            Listeners:
            [
                new ApiListenerOptions("orders", Port: 0, ScopeIdentifier: Orders, Audience: Orders,
                    AdmittedClients: [IdpHarness.Client1]),
                new ApiListenerOptions("reports", Port: 0, ScopeIdentifier: Reports, Audience: null,
                    AdmittedClients: [IdpHarness.Client2]),
            ]),
            TestContext.Current.CancellationToken);

        return new MockApiHarness(idp, api, new HttpClient());
    }

    /// <summary>A call to one listener, carrying a bearer token.</summary>
    public async Task<HttpResponseMessage> CallAsync(string listener, string path, string? token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(Api.BaseAddressOf(listener), path));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>App 1's access token, bound to the orders API as the fixture's definition binds it.</summary>
    public async Task<string> App1TokenAsync(string scope = "openid profile " + OrdersRead)
    {
        Login login = await Idp.LoginAsync(
            IdpHarness.Client1, IdpHarness.Secret1, IdpHarness.Redirect1, resource: Orders, scope: scope);

        return login.AccessToken;
    }

    /// <summary>App 2's access token, with no resource binding, as its definition has none.</summary>
    public async Task<string> App2TokenAsync(string scope = "openid profile " + ReportsRead)
    {
        Login login = await Idp.LoginAsync(
            IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2, resource: null, scope: scope);

        return login.AccessToken;
    }

    public static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Api.DisposeAsync();
        await Idp.DisposeAsync();
    }
}
