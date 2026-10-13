using System.Net;
using System.Net.Http.Json;
using System.Text;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The echo upstream. Workstream 4 asserts header stripping against it (N-11, N-12, T-C3): the proxy
/// forwards, and the test reads what actually arrived. Workstream 6's harness (task 6.1) needs the
/// same thing behind a real listener.
/// </summary>
public sealed class EchoUpstreamTests
{
    [Fact]
    public async Task The_handler_records_the_method_and_path_that_arrived()
    {
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        await client.PostAsync("/orders/42", new StringContent("{}"), Ct);

        EchoedRequest received = Assert.Single(upstream.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/orders/42", received.Path);
    }

    [Fact]
    public async Task The_handler_records_the_headers_that_arrived()
    {
        // The positive half of header stripping: a test has to be able to see a header that was
        // forwarded, or "it was stripped" proves nothing.
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };
        client.DefaultRequestHeaders.Add("Authorization", "Bearer forwarded-token");

        await client.GetAsync("/orders", Ct);

        EchoedRequest received = Assert.Single(upstream.Requests);
        Assert.Equal("Bearer forwarded-token", Assert.Single(received.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_header_that_never_arrived_is_absent_rather_than_empty()
    {
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        await client.GetAsync("/orders", Ct);

        EchoedRequest received = Assert.Single(upstream.Requests);
        Assert.False(received.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task The_handler_records_the_body_that_arrived()
    {
        // Nothing upstream of the forwarder may consume the body (finding R-E7, task 4.4).
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        await client.PostAsync("/orders", new StringContent("{\"id\":42}", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal("{\"id\":42}", Assert.Single(upstream.Requests).Body);
    }

    [Fact]
    public async Task The_handler_answers_200_by_default()
    {
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        HttpResponseMessage response = await client.GetAsync("/orders", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_handler_echoes_what_arrived_in_its_response_body()
    {
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        HttpResponseMessage response = await client.GetAsync("/orders/42?page=2", Ct);

        EchoedRequest? echoed = await response.Content.ReadFromJsonAsync<EchoedRequest>(Ct);
        Assert.NotNull(echoed);
        Assert.Equal("GET", echoed.Method);
        Assert.Equal("/orders/42", echoed.Path);
        Assert.Equal("?page=2", echoed.Query);
    }

    [Fact]
    public async Task An_arranged_response_is_returned_instead()
    {
        // So a test can drive the proxy's handling of an upstream failure.
        EchoUpstream upstream = new()
        {
            Respond = _ => new EchoResponse(HttpStatusCode.ServiceUnavailable, "upstream is down"),
        };
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        HttpResponseMessage response = await client.GetAsync("/orders", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upstream is down", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Requests_are_recorded_in_order()
    {
        EchoUpstream upstream = new();
        using HttpClient client = new(upstream.Handler) { BaseAddress = new Uri("https://orders.internal") };

        await client.GetAsync("/first", Ct);
        await client.GetAsync("/second", Ct);

        Assert.Equal(["/first", "/second"], upstream.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task The_same_upstream_behind_a_real_listener_records_the_same_way()
    {
        // Task 6.1's harness needs a real address, not a handler.
        EchoUpstream upstream = new();
        await using EchoUpstreamListener listener = await upstream.ListenAsync(Ct);
        using HttpClient client = new() { BaseAddress = listener.BaseAddress };

        HttpResponseMessage response = await client.GetAsync("/orders/42", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/orders/42", Assert.Single(upstream.Requests).Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
