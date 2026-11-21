using System.Diagnostics;
using System.Net;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// The HTTP/1.1 connection bound of one app's API, <see cref="ProxyLimits.MaxHttp11ConnectionsPerApi"/>,
/// proven over a cleartext loopback upstream (#738; #715 Q7; <c>contracts/upstream-contract.md</c> ->
/// Timeouts and failures). With the bound's number of identified requests held open, kept open by the
/// upstream's body traffic, one more request to the same API waits for a connection that never frees,
/// and is answered as any other silence before the headers: 504 <c>upstream_timeout</c>, with no
/// upstream header, and the upstream never sees it. The bound is the production constant, and it is
/// per app API: two apps that name the same upstream each hold it (#715 Q3). The composed rows are
/// workstream 6's; these carry no test id (#738 Q5).
///
/// If the extra request ever answers 502, or not within the window, #738's stop rule holds: the work
/// stops and the contract sentence goes back to a change request; <c>UpstreamFailure</c> is not
/// changed to re-map it (#715 Q8). A held request that ends early fails the test with the upstream's
/// timeline, to be diagnosed, not retried (#738 Q11).
/// </summary>
public sealed class ConnectionBoundTests
{
    /// <summary>The entry's idle limit: eight of the upstream's beats inside it (#738 Q6).</summary>
    private const int TimeoutSeconds = 2;

    private const int N = ProxyLimits.MaxHttp11ConnectionsPerApi;

    /// <summary>The upstream path of the request over the bound, after the proxy removes <c>/api/orders</c> (#738 Q8).</summary>
    private const string Extra = "/extra";

    /// <summary>
    /// One operation deadline for everything a test sends, so a hang is a failure (#738 Q10). It starts
    /// once both hosts are listening: starting them is every test's cost, and on a loaded machine it
    /// alone took 44 s on 2026-11-16, which is no evidence about the bound.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Clean-up's own budget, not linked to the test's token, so a cancelled test still drains what it
    /// started and cannot wait without end doing so (#738 Q10).
    /// </summary>
    private static readonly TimeSpan CleanUpBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When the extra request's 504 may arrive, from its send, on a monotonic clock (#738 Q7). The
    /// lower edge, the idle limit less one beat, shows it waited for a connection rather than failing
    /// fast; the upper edge, 5 s of slack past it, is the criterion's "within" on a loaded runner, and
    /// is no strict 2 s bound.
    /// </summary>
    private static readonly TimeSpan Earliest = TimeSpan.FromSeconds(TimeoutSeconds) - HoldingUpstream.Beat;

    private static readonly TimeSpan Latest = TimeSpan.FromSeconds(TimeoutSeconds + 5);

    [Fact]
    public async Task One_request_over_the_bound_of_an_api_is_504_upstream_timeout_and_never_reaches_the_upstream()
    {
        await using HoldingUpstream upstream = await HoldingUpstream.StartAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, AppNaming("app1", upstream));

        await HoldTheBoundThenSendOneMoreAsync(upstream, gateway, ["app1"]);
    }

    [Fact]
    public async Task Two_apps_naming_the_same_upstream_each_hold_the_bound_and_each_one_more_is_504()
    {
        await using HoldingUpstream upstream = await HoldingUpstream.StartAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, AppNaming("app1", upstream), AppNaming("app2", upstream));

        // One extra per app, at once, with both apps' held requests still held: each app's own pool
        // refuses its own (#738 Q9).
        await HoldTheBoundThenSendOneMoreAsync(upstream, gateway, ["app1", "app2"]);
    }

    /// <summary>
    /// Holds N identified requests to each app's <c>orders</c>, waits until all are held on distinct
    /// live connections, sends one more to each app, and checks the held ones stayed held through the
    /// extra answers; then releases them and checks each ends 200 with its whole body.
    /// </summary>
    private static async Task HoldTheBoundThenSendOneMoreAsync(
        HoldingUpstream upstream, ProxyHost gateway, string[] appIds)
    {
        using HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Deadline);
        CancellationToken ct = deadline.Token;

        (string AppId, string Path)[] holds =
            [.. appIds.SelectMany(appId => Enumerable.Range(0, N).Select(i => (appId, $"/held/{appId}-{i}")))];
        string[] heldPaths = [.. holds.Select(hold => hold.Path)];
        List<Task<HttpResponseMessage>> held = [];

        try
        {
            held.AddRange(holds.Select(hold => client.SendAsync(
                gateway.Request(HttpMethod.Get, hold.AppId + TestApps.HostSuffix, "/api/orders" + hold.Path),
                HttpCompletionOption.ResponseHeadersRead,
                ct)));

            HttpResponseMessage[] heads = await Task.WhenAll(held);
            Assert.All(heads, head =>
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.True(head.Headers.Contains(HoldingUpstream.MarkerHeader));
            });
            await upstream.WaitForHeldAsync(heldPaths, ct);
            AssertStillHeld(upstream, heldPaths);

            await Task.WhenAll(appIds.Select(appId => AssertOneMoreIs504Async(gateway, client, appId, upstream, ct)));

            // The same requests are held now as before the extras were sent: no slot freed while they
            // waited, and the peak is the bound, not one past it.
            AssertStillHeld(upstream, heldPaths);
            Assert.Equal(heldPaths.Length, upstream.PeakConnections);
            AssertOnlyTheHeldRequestsArrivedOverHttp11(upstream, heldPaths);

            upstream.Release();
            foreach (HttpResponseMessage head in heads)
            {
                Assert.Matches(
                    $"^\\{HoldingUpstream.BeatByte}+{HoldingUpstream.EndOfBody}$",
                    await head.Content.ReadAsStringAsync(ct));
            }

            await upstream.HandlersFinished().WaitAsync(ct);
            Assert.False(upstream.AnyEndedEarly, upstream.Describe());
            AssertOnlyTheHeldRequestsArrivedOverHttp11(upstream, heldPaths);
        }
        finally
        {
            await CleanUpAsync(upstream, deadline, held);
        }
    }

    /// <summary>The request over the bound: 504 <c>upstream_timeout</c>, no upstream header, inside the window.</summary>
    private static async Task AssertOneMoreIs504Async(
        ProxyHost gateway, HttpClient client, string appId, HoldingUpstream upstream, CancellationToken ct)
    {
        // Headers only, so a request the gateway let through shows as the upstream's held 200 at once
        // rather than as a body that never ends.
        Stopwatch waited = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, appId + TestApps.HostSuffix, "/api/orders" + Extra),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        waited.Stop();

        // A status other than 504 comes with the timeline: a held request that ended early, freeing a
        // slot, and a contract mismatch look alike from here (#738 Q11).
        Assert.True(
            response.StatusCode == HttpStatusCode.GatewayTimeout,
            $"The request over the bound answered {(int)response.StatusCode}.\n{upstream.Describe()}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("upstream_timeout", await Problem.CodeOf(response));

        // No upstream headers: the marker is on every response the upstream sends (#738 Q8).
        Assert.False(response.Headers.Contains(HoldingUpstream.MarkerHeader));
        Assert.False(response.Content.Headers.Contains(HoldingUpstream.MarkerHeader));

        Assert.InRange(waited.Elapsed, Earliest, Latest);
        Assert.DoesNotContain(upstream.Requests, seen => seen.Path == Extra);
    }

    /// <summary>
    /// Every one of <paramref name="heldPaths"/> is held now, each on its own connection, that many
    /// connections are open, and none ended early. A final peak or count alone would not show this
    /// (#738 Q9).
    /// </summary>
    private static void AssertStillHeld(HoldingUpstream upstream, string[] heldPaths)
    {
        IReadOnlyDictionary<string, string> now = upstream.Held;
        bool held = now.Count == heldPaths.Length
            && heldPaths.All(now.ContainsKey)
            && now.Values.Distinct(StringComparer.Ordinal).Count() == heldPaths.Length
            && upstream.OpenConnections == heldPaths.Length
            && !upstream.AnyEndedEarly;
        Assert.True(held, "The held requests are not all still held on their own connections.\n" + upstream.Describe());
    }

    private static void AssertOnlyTheHeldRequestsArrivedOverHttp11(HoldingUpstream upstream, string[] heldPaths)
    {
        IReadOnlyList<HoldingUpstream.Event> requests = upstream.Requests;

        // Each held path once and nothing else, so the protocol check below covers every request; and
        // RequestVersionOrLower fell back to HTTP/1.1 over cleartext, the protocol the bound bounds.
        Assert.Equal(heldPaths.Order(StringComparer.Ordinal), requests.Select(seen => seen.Path!).Order(StringComparer.Ordinal));
        Assert.All(requests, seen => Assert.Equal("HTTP/1.1", seen.Protocol));
    }

    /// <summary>
    /// Releases the upstream, cancels whatever is still being sent, and drains every started request and
    /// every upstream handler under <see cref="CleanUpBudget"/>. It throws nothing, so a test's own
    /// failure is the one reported (#738 Q10); on the success path everything is already drained.
    /// </summary>
    private static async Task CleanUpAsync(
        HoldingUpstream upstream, CancellationTokenSource deadline, List<Task<HttpResponseMessage>> held)
    {
        upstream.Release();
        await deadline.CancelAsync();

        _ = await Task.WhenAny(Task.WhenAll(held), Task.Delay(CleanUpBudget, CancellationToken.None));
        foreach (Task<HttpResponseMessage> request in held.Where(request => request.IsCompletedSuccessfully))
        {
            (await request).Dispose();
        }

        _ = await Task.WhenAny(upstream.HandlersFinished(), Task.Delay(CleanUpBudget, CancellationToken.None));
    }

    private static AppDefinition AppNaming(string appId, HoldingUpstream upstream) =>
        TestApps.Entry(appId, apis: [
            TestApps.Api("orders", upstream.BaseAddress.AbsoluteUri, timeoutSeconds: TimeoutSeconds),
        ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
