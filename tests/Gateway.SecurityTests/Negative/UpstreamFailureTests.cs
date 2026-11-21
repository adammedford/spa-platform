using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Proxy;
using Platform.Registry;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-33 to N-35, the composed upstream failure rows (<c>contracts/test-ids.md</c>; change request
/// #734; #715 Q9): an upstream that refuses the connection is 502 <c>upstream_unavailable</c>, one that
/// stays silent before its headers past the entry's <c>timeoutSeconds</c> is 504
/// <c>upstream_timeout</c>, and one call over the HTTP/1.1 connection bound of one app's API is 504
/// <c>upstream_timeout</c> with no upstream headers and never reaches the upstream
/// (<c>contracts/upstream-contract.md</c> -> Timeouts and failures). Workstream 4 proves the same three
/// in its own project, under no id; this is the composed gateway, from <c>Program.cs</c>, behind a
/// signed-in browser, because only a call that carries a session is ever forwarded.
///
/// <para>Each test lists its own <see cref="ControlledUpstream"/> under <see cref="ApiName"/>, with a
/// short <c>timeoutSeconds</c>, the way <see cref="UpstreamResponseTests"/> lists its hostile one;
/// <c>fixtures/</c> is not changed. None of the three is a refusal by a rule, so each asserts that its
/// request line carries no rule id.</para>
/// </summary>
public sealed class UpstreamFailureTests
{
    /// <summary>The API name the controlled upstream is listed under, for these tests only.</summary>
    private const string ApiName = "controlled";

    /// <summary>The bound under test: the production constant, read rather than copied (#715 Q6, Q9).</summary>
    private const int N = ProxyLimits.MaxHttp11ConnectionsPerApi;

    /// <summary>N-34's idle limit: the schema's least.</summary>
    private const int SilentTimeoutSeconds = 1;

    /// <summary>N-35's idle limit: eight of the upstream's beats inside it, as workstream 4's proof has (#738 Q6).</summary>
    private const int BoundTimeoutSeconds = 2;

    /// <summary>The upstream path of the call over the bound, after the proxy removes <c>/api/controlled</c>.</summary>
    private const string Extra = "/extra";

    /// <summary>
    /// One deadline for everything N-35 sends once the gateway is up and the browser signed in, so a hang
    /// is a failure. Starting the hosts is every test's cost and no evidence about the bound.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>Clean-up's own budget, not linked to the test's token, so a failed test still drains what it started.</summary>
    private static readonly TimeSpan CleanUpBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The slack past the idle limit a 504 may take on a loaded runner. Paired with a lower edge, it
    /// shows the answer came from the timeout and not from a fast failure; it is not a strict bound.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task An_upstream_that_refuses_the_connection_is_502_upstream_unavailable()
    {
        await using ControlledUpstream upstream = await ControlledUpstream.StartAsync(UpstreamBehaviour.Refuse, Suite.Ct);
        await using GatewayHarness harness = await StartWith(upstream, timeoutSeconds: 30);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);

        BrowserResponse response = await browser.GetAsync($"/api/{ApiName}/thing", ct: Suite.Ct);

        AssertProblem("upstream_unavailable", response);
        Assert.Null(await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
    }

    [Fact]
    public async Task An_upstream_silent_before_its_headers_past_the_timeout_is_504_upstream_timeout()
    {
        await using ControlledUpstream upstream = await ControlledUpstream.StartAsync(UpstreamBehaviour.Silent, Suite.Ct);
        await using GatewayHarness harness = await StartWith(upstream, SilentTimeoutSeconds);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);

        Stopwatch waited = Stopwatch.StartNew();
        BrowserResponse response = await browser.GetAsync($"/api/{ApiName}/thing", ct: Suite.Ct);
        waited.Stop();

        AssertProblem("upstream_timeout", response);
        Assert.Null(await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));

        // The call reached the upstream and the answer waited for the idle limit: a refusal, or a gateway
        // that answered without forwarding, would be quick and leave the upstream's record empty.
        Assert.Equal("/thing", Assert.Single(upstream.Requests).Path);
        Assert.InRange(
            waited.Elapsed,
            TimeSpan.FromSeconds(SilentTimeoutSeconds) - TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(SilentTimeoutSeconds) + Slack);
    }

    /// <summary>
    /// The staged proof of #715 Q7, as workstream 4's <c>ConnectionBoundTests</c> runs it: N identified
    /// calls held open on N connections by the upstream's body traffic, then one more, which waits for a
    /// connection that never frees and is answered as silence before the headers. A simultaneous N + 1
    /// burst would not show which call was the one left out.
    /// </summary>
    [Fact]
    public async Task One_call_over_the_connection_bound_is_504_upstream_timeout_before_headers_and_the_upstream_never_sees_it()
    {
        await using ControlledUpstream upstream = await ControlledUpstream.StartAsync(UpstreamBehaviour.Trickle, Suite.Ct);
        await using GatewayHarness harness = await StartWith(upstream, BoundTimeoutSeconds);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Suite.Ct);
        deadline.CancelAfter(Deadline);
        CancellationToken ct = deadline.Token;

        string[] heldPaths = [.. Enumerable.Range(0, N).Select(i => $"/held/{i}")];
        List<Task<BrowserResponse>> held = [];
        try
        {
            // The test browser reads each body whole, so these complete only once the upstream is
            // released; whether they are held is read from the upstream's side.
            held.AddRange(heldPaths.Select(path => browser.GetAsync($"/api/{ApiName}{path}", ct: ct)));
            await upstream.WaitForHeldAsync(heldPaths, ct);
            AssertStillHeld(upstream, heldPaths);

            // Held calls write their request lines when they end, so the extra call's is the one new line.
            int before = await Observed.SettledCountAsync(harness, ct);

            // The extra call races the upstream's record of it. A call the gateway let through would be
            // held and trickled like the others, and the test browser would wait on its body, so the
            // race is what makes "the upstream never sees it" fail by name rather than at the deadline.
            Stopwatch waited = Stopwatch.StartNew();
            Task<BrowserResponse> extraCall = browser.GetAsync($"/api/{ApiName}{Extra}", ct: ct);
            held.Add(extraCall);
            using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task arrived = upstream.WaitForPathAsync(Extra, watching.Token);
            Task first = await Task.WhenAny(extraCall, arrived);
            await watching.CancelAsync();
            Assert.True(first == extraCall, "The call over the bound reached the upstream.\n" + upstream.Describe());
            BrowserResponse extra = await extraCall;
            waited.Stop();

            Assert.True(
                extra.StatusCode == HttpStatusCode.GatewayTimeout,
                $"The call over the bound answered {(int)extra.StatusCode}.\n{upstream.Describe()}");
            AssertProblem("upstream_timeout", extra);
            Assert.Null(extra.Header(ControlledUpstream.MarkerHeader));
            Assert.InRange(
                waited.Elapsed,
                TimeSpan.FromSeconds(BoundTimeoutSeconds) - ControlledUpstream.Beat,
                TimeSpan.FromSeconds(BoundTimeoutSeconds) + Slack);
            Assert.Null(await Observed.RuleOfOneRequestAsync(harness, before, ct));

            // The same calls are held now as before the extra was sent, the peak is the bound and not one
            // past it, and the upstream saw the held calls only, each over HTTP/1.1, the protocol the bound
            // bounds.
            AssertStillHeld(upstream, heldPaths);
            Assert.Equal(N, upstream.PeakConnections);
            AssertOnlyTheHeldCallsArrived(upstream, heldPaths);

            upstream.Release();
            foreach (BrowserResponse response in await Task.WhenAll(held.Where(call => call != extraCall)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                // The control on the extra call's missing marker: the gateway forwards the header, so its
                // absence there means the upstream wrote nothing of that answer.
                Assert.Equal("1", response.Header(ControlledUpstream.MarkerHeader));
                Assert.Matches($"^\\{ControlledUpstream.BeatByte}+{ControlledUpstream.EndOfBody}$", response.Body);
            }

            await upstream.HandlersFinished().WaitAsync(ct);
            Assert.False(upstream.AnyEndedEarly, upstream.Describe());
            AssertOnlyTheHeldCallsArrived(upstream, heldPaths);
        }
        finally
        {
            await CleanUpAsync(upstream, deadline, held);
        }
    }

    [Fact]
    public async Task The_refusing_upstream_really_refuses_the_connection()
    {
        // The guard on N-33's double. A 502 also answers an upstream that sends its headers and falls
        // silent, so the row proves a refusal only while this double refuses; this connects to it
        // directly, with no gateway in the way.
        await using ControlledUpstream upstream = await ControlledUpstream.StartAsync(UpstreamBehaviour.Refuse, Suite.Ct);
        Uri origin = new(upstream.Origin);
        using TcpClient client = new();

        SocketException refused = await Assert.ThrowsAsync<SocketException>(
            () => client.ConnectAsync(IPAddress.Loopback, origin.Port, Suite.Ct).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    private static void AssertProblem(string code, BrowserResponse response)
    {
        Contract.ErrorRow expected = Contract.Error(code);
        Assert.Equal(expected.Status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every one of <paramref name="heldPaths"/> is held now, each on its own connection, that many
    /// connections are open, and none ended early. A final peak or count alone would not show this.
    /// </summary>
    private static void AssertStillHeld(ControlledUpstream upstream, string[] heldPaths)
    {
        IReadOnlyDictionary<string, string> now = upstream.Held;
        bool held = now.Count == heldPaths.Length
            && heldPaths.All(now.ContainsKey)
            && now.Values.Distinct(StringComparer.Ordinal).Count() == heldPaths.Length
            && upstream.OpenConnections == heldPaths.Length
            && !upstream.AnyEndedEarly;
        Assert.True(held, "The held calls are not all still held on their own connections.\n" + upstream.Describe());
    }

    private static void AssertOnlyTheHeldCallsArrived(ControlledUpstream upstream, string[] heldPaths)
    {
        IReadOnlyList<ControlledUpstream.Event> requests = upstream.Requests;
        Assert.DoesNotContain(requests, seen => seen.Path == Extra);
        Assert.Equal(heldPaths.Order(StringComparer.Ordinal), requests.Select(seen => seen.Path!).Order(StringComparer.Ordinal));
        Assert.All(requests, seen => Assert.Equal("HTTP/1.1", seen.Protocol));
    }

    /// <summary>
    /// Releases the upstream, cancels whatever is still being sent, and drains every started call and
    /// every upstream handler under <see cref="CleanUpBudget"/>. It throws nothing, so a test's own
    /// failure is the one reported.
    /// </summary>
    private static async Task CleanUpAsync(
        ControlledUpstream upstream, CancellationTokenSource deadline, List<Task<BrowserResponse>> held)
    {
        upstream.Release();
        await deadline.CancelAsync();
        _ = await Task.WhenAny(Task.WhenAll(held), Task.Delay(CleanUpBudget, CancellationToken.None));
        _ = await Task.WhenAny(upstream.HandlersFinished(), Task.Delay(CleanUpBudget, CancellationToken.None));
    }

    /// <summary>
    /// A cell whose app 1 lists <paramref name="upstream"/> under <see cref="ApiName"/> with
    /// <paramref name="timeoutSeconds"/>. <c>Policy:AllowedUpstreams</c> is read when the gateway starts,
    /// so the upstream comes first and the cell is configured around it; the entry is then published as
    /// an operator's would be, and the validator refuses an upstream the policy does not name.
    /// </summary>
    private static async Task<GatewayHarness> StartWith(ControlledUpstream upstream, int timeoutSeconds)
    {
        // With no echo API the harness lists two allowed upstreams, at indices 0 and 1, so this adds a third.
        GatewayHarnessOptions options = new()
        {
            EchoApi = null,
            Configuration = new Dictionary<string, string?>
            {
                ["Policy:AllowedUpstreams:2"] = upstream.Origin,
            },
        };

        GatewayHarness harness = await Suite.StartAsync(options, Suite.Ct);
        try
        {
            RefreshOutcome outcome = await harness.UpdateRegistryAsync(
                document => document["apps"]!.AsArray()
                    .Single(app => app!["appId"]!.GetValue<string>() == "app1")!
                    .AsObject()["apis"]!.AsArray()
                    .Add(new JsonObject
                    {
                        ["name"] = ApiName,
                        ["upstream"] = upstream.Origin,
                        ["timeoutSeconds"] = timeoutSeconds,
                    }),
                Suite.Ct);

            Assert.Equal(RefreshOutcome.Swapped, outcome);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }
}
