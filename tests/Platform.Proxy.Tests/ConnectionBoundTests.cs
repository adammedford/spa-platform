using System.Diagnostics;
using System.Net;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// The connection bound on the wire (#738; <c>contracts/upstream-contract.md</c> -> Timeouts and
/// failures): with <see cref="ProxyLimits.MaxConnectionsPerApi"/> HTTP/1.1 connections held open to
/// one of an app's APIs, one more call to it waits for a connection inside the entry's idle limit and
/// is 504 <c>upstream_timeout</c> before any upstream header, and the upstream never sees it. The bound
/// is the production constant, not a test override, and it is per app API: two apps that name the same
/// upstream each hold the bound to it (#715 Q3, Q7).
///
/// Every wait runs under <see cref="Bound"/>, so a hang is an <see cref="OperationCanceledException"/>
/// and a failure, not a run that goes on.
/// </summary>
public sealed class ConnectionBoundTests
{
    /// <summary>The entry's idle limit. Thirty drips of <see cref="HeldUpstream.Drip"/> fit inside it.</summary>
    private const int TimeoutSeconds = 3;

    private const string OverTheBound = "/over-the-bound";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>How far past the idle limit the call over the bound may answer and still be "within" it.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private static int N => ProxyLimits.MaxConnectionsPerApi;

    [Fact]
    public async Task One_call_over_the_bound_to_an_apps_api_is_504_upstream_timeout_before_headers_and_the_upstream_never_sees_it()
    {
        await using HeldUpstream upstream = await HeldUpstream.StartAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, App("app1", upstream));
        using HttpClient client = new();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(Bound);
        List<HttpResponseMessage> held = [];

        try
        {
            held.AddRange(await HoldAsync(client, gateway, "app1", bound.Token));
            await upstream.WaitForConnectionsAsync(N, bound.Token);

            HttpResponseMessage over = await OverTheBoundAsync(client, gateway, "app1", bound.Token);

            Assert.Equal(N, upstream.CurrentConnections);
            Assert.Equal(N, upstream.PeakConnections);
            Assert.Equal(N, upstream.Paths.Count);
            Assert.All(upstream.Protocols, protocol => Assert.Equal("HTTP/1.1", protocol));
            Assert.DoesNotContain(OverTheBound, upstream.Paths);

            upstream.Release();
            await DrainAsync(held, bound.Token);
            Assert.DoesNotContain(OverTheBound, upstream.Paths);
            over.Dispose();
        }
        finally
        {
            upstream.Release();
            held.ForEach(response => response.Dispose());
        }
    }

    [Fact]
    public async Task Two_apps_whose_apis_name_the_same_upstream_each_hold_the_bound_to_it()
    {
        await using HeldUpstream upstream = await HeldUpstream.StartAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, App("app1", upstream), App("app2", upstream));
        using HttpClient client = new();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(Bound);
        List<HttpResponseMessage> held = [];

        try
        {
            HttpResponseMessage[][] both = await Task.WhenAll(
                HoldAsync(client, gateway, "app1", bound.Token),
                HoldAsync(client, gateway, "app2", bound.Token));
            held.AddRange(both.SelectMany(responses => responses));
            await upstream.WaitForConnectionsAsync(2 * N, bound.Token);

            HttpResponseMessage[] over = await Task.WhenAll(
                OverTheBoundAsync(client, gateway, "app1", bound.Token),
                OverTheBoundAsync(client, gateway, "app2", bound.Token));

            Assert.Equal(2 * N, upstream.CurrentConnections);
            Assert.Equal(2 * N, upstream.PeakConnections);
            Assert.Equal(2 * N, upstream.Paths.Count);
            Assert.All(upstream.Protocols, protocol => Assert.Equal("HTTP/1.1", protocol));
            Assert.DoesNotContain(OverTheBound, upstream.Paths);

            upstream.Release();
            await DrainAsync(held, bound.Token);
            Assert.DoesNotContain(OverTheBound, upstream.Paths);
            Array.ForEach(over, response => response.Dispose());
        }
        finally
        {
            upstream.Release();
            held.ForEach(response => response.Dispose());
        }
    }

    private static AppDefinition App(string appId, HeldUpstream upstream) =>
        TestApps.Entry(appId, apis: [
            TestApps.Api("orders", upstream.BaseAddress.AbsoluteUri, timeoutSeconds: TimeoutSeconds),
        ]);

    /// <summary>N calls to the app's <c>orders</c>, each answered with the upstream's headers and held.</summary>
    private static async Task<HttpResponseMessage[]> HoldAsync(
        HttpClient client, ProxyHost gateway, string appId, CancellationToken ct)
    {
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, N).Select(i =>
            client.SendAsync(
                gateway.Request(HttpMethod.Get, appId + ".spa-poc.test", $"/api/orders/held/{i}"),
                HttpCompletionOption.ResponseHeadersRead,
                ct)));

        // The marker on a held response is what lets its absence on the call over the bound mean
        // something.
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains(HeldUpstream.MarkerHeader));
        });
        return responses;
    }

    /// <summary>The call over the bound: 504 <c>upstream_timeout</c>, no upstream header, inside the idle limit.</summary>
    private static async Task<HttpResponseMessage> OverTheBoundAsync(
        HttpClient client, ProxyHost gateway, string appId, CancellationToken ct)
    {
        // Headers only, so a call the gateway let through shows as the upstream's held 200 at once
        // rather than as a body that never ends.
        Stopwatch elapsed = Stopwatch.StartNew();
        HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, appId + ".spa-poc.test", "/api/orders" + OverTheBound),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("upstream_timeout", await Problem.CodeOf(response));
        Assert.False(response.Headers.Contains(HeldUpstream.MarkerHeader));
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(TimeoutSeconds) + Grace);
        return response;
    }

    /// <summary>Reads every held response to its end, which it reaches only once the upstream is released.</summary>
    private static async Task DrainAsync(IEnumerable<HttpResponseMessage> held, CancellationToken ct) =>
        await Task.WhenAll(held.Select(response => response.Content.ReadAsByteArrayAsync(ct)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
