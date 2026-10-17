using System.Net;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Registry;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-10, <b>partial</b>: "Call an API name the app does not list, and paths built to escape the
/// prefix -> 404 <c>unknown_api</c> for the unlisted name, 400 or 404 for the escape paths. No
/// upstream logs the request." (docs/plan/04-tests.md.)
///
/// <para>The class fixture's refusals carry no session. Its control,
/// <see cref="A_listed_api_name_does_reach_its_upstream"/>, signs in, because only a call with a
/// session is forwarded at all: the proxy's token step refuses one without before an upstream is
/// reached. <see cref="A_signed_in_caller_meets_the_same_refusals_and_no_upstream_sees_the_request"/>
/// repeats both refusals for a caller with a live session, on a cell of its own, so a session is not
/// what stands between an attacker and an unlisted upstream.</para>
///
/// <para><b>Why the unlisted name cannot reach an upstream.</b> ADR-0006: YARP's routes are
/// generated from the registry snapshot, one per app and API, and nothing reads the request to
/// choose a destination. A name the entry does not list therefore matches no route and lands on the
/// catch-all behind them, which is the 404 (findings T-C9, T-C10). The test shows the consequence a
/// client can see; the mechanism is the proxy project's own tests.</para>
///
/// <para><b>Why the raw socket for the escape paths.</b> A dot segment, an encoded slash and a
/// backslash are exactly what <see cref="Uri"/> normalises away, so a client library would send a
/// different request from the one the row names. The gate reads <c>RawTarget</c>, before any
/// decoding, which only a socket can set.</para>
/// </summary>
public sealed class UnknownApiTests(UnknownApiTests.Cell cell) : IClassFixture<UnknownApiTests.Cell>
{
    /// <summary>One harness for this class: the registry it starts with lists the names these rows avoid.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>
    /// Paths built to escape the <c>/api/{name}</c> prefix, each a shape finding T-C10 names: a dot
    /// segment, a dot segment with a <c>;</c> parameter some servers strip before resolving it, the
    /// escaped forms of <c>.</c>, <c>/</c> and <c>\</c>, a literal backslash, and a doubled slash.
    /// The target is sent exactly as written.
    /// </summary>
    public static TheoryData<string> EscapeTargets => new(Escapes);

    private static readonly string[] Escapes =
        [
            "/api/echo/../../secret",
            "/api/echo/..;/secret",
            "/api/echo/%2e%2e/secret",
            "/api/echo/%2fsecret",
            "/api/echo/%5csecret",
            "/api/echo\\..\\secret",
            "/api/echo//secret",
            "/api/./echo/thing",
        ];

    [Fact]
    public async Task An_api_name_the_app_does_not_list_is_404_unknown_api()
    {
        using TestBrowser browser = Harness.CreateBrowser();
        await SignedIn.OnPageOfAsync(browser, Harness.App("app1"));

        int upstreams = Harness.Echo.Requests.Count;
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        BrowserResponse response = await browser.GetAsync("/api/nosuch/thing", ct: Suite.Ct);

        Contract.ErrorRow expected = Contract.Error("The app lists no upstream under that name");
        Assert.Equal(expected.Status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.Body, StringComparison.Ordinal);

        Assert.Equal(RuleIds.ApiUnknown, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
        AssertNoUpstreamSaw(response.Body, upstreams);
    }

    [Fact]
    public async Task An_api_name_another_app_lists_is_still_unlisted_for_this_one()
    {
        // The tenancy half of the row: a route is matched on the app's host as well as the path
        // (ADR-0006), so a name one app lists is not reachable on another app's host.
        //
        // Its own harness, because the input has to be published and the class fixture's registry is
        // shared. Both fixture apps happen to list the same three API names, so no fixture can
        // supply this input, and fixtures/ is workstream 5's: the entry therefore arrives the way an
        // operator's would, by publishing a revision that takes one name off app 2
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);

        // The echo API, because it answers every path. The mock API's listeners answer 404 to a path
        // they do not map, which would be indistinguishable from the proxy's own 404 and would make
        // the control below prove nothing.
        string name = harness.Options.EchoApi
            ?? throw new InvalidOperationException("this row needs an upstream that answers every path.");

        RefreshOutcome outcome = await harness.UpdateRegistryAsync(
            document => Entry(document, "app2")["apis"] = new JsonArray(
                [.. Entry(document, "app2")["apis"]!.AsArray()
                    .Where(api => api!["name"]!.GetValue<string>() != name)
                    .Select(api => api!.DeepClone())]),
            Suite.Ct);
        Assert.Equal(RefreshOutcome.Swapped, outcome);

        using TestBrowser browser = harness.CreateBrowser();

        // Still listed on app 1, and reachable there with a session: the name itself is not what was
        // taken away, and the upstream behind it is the same one app 2 can no longer reach.
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        int reachedFromApp1 = harness.Echo.Requests.Count;
        BrowserResponse onApp1 = await browser.GetAsync($"/api/{name}/thing", ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, onApp1.StatusCode);
        Assert.Equal(reachedFromApp1 + 1, harness.Echo.Requests.Count);

        // Signed in to app 2 as well, so what refuses the call there is the missing route and not
        // the missing session.
        await harness.SignInAsync(browser, "app2", Suite.Ct);
        int echoed = harness.Echo.Requests.Count;
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);

        BrowserResponse onApp2 = await browser.GetAsync($"/api/{name}/thing", ct: Suite.Ct);

        Assert.Equal(HttpStatusCode.NotFound, onApp2.StatusCode);
        Assert.Equal(RuleIds.ApiUnknown, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        Assert.Equal(echoed, harness.Echo.Requests.Count);
        Assert.DoesNotContain("\"api\":", onApp2.Body, StringComparison.Ordinal);
    }

    /// <summary>One app's entry in a registry document the test is editing.</summary>
    private static JsonObject Entry(JsonObject document, string appId) =>
        document["apps"]!.AsArray().Single(app => app!["appId"]!.GetValue<string>() == appId)!.AsObject();

    [Theory]
    [MemberData(nameof(EscapeTargets))]
    public async Task A_path_built_to_escape_the_api_prefix_is_refused_under_path_invalid(string target)
    {
        int upstreams = Harness.Echo.Requests.Count;
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        // Sec-Fetch-Site as a same-origin page would send it, so the only thing wrong with the
        // request is its path: the gate judges the path's form before it judges the /api row, so a
        // refusal here is path.invalid and not the fetch-site rule in disguise.
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", target, Harness.App("app1").Host,
            [("Sec-Fetch-Site", FetchMetadata.SameOrigin)], Suite.Ct);

        Contract.ErrorRow expected = Contract.Error("The path failed normalisation");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);

        Assert.Equal(RuleIds.PathInvalid, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
        AssertNoUpstreamSaw(RawBody.Text(response), upstreams);
    }

    [Fact]
    public async Task An_escaped_null_byte_in_an_api_path_never_reaches_the_pipeline()
    {
        // Kestrel refuses an escaped NUL itself, before any middleware runs, so this one is answered
        // by the server rather than by a stage: there is no rule id to read, and the contract's "400
        // or 404" is met by the server's own 400. Asserted separately from the rows above for
        // exactly that reason — a test that demanded a rule id here would be asserting against the
        // wrong component.
        int upstreams = Harness.Echo.Requests.Count;
        int lines = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/api/echo/x%00y", Harness.App("app1").Host,
            [("Sec-Fetch-Site", FetchMetadata.SameOrigin)], Suite.Ct);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(upstreams, Harness.Echo.Requests.Count);

        // No stage ran, so no request line was written. Read after the assertions above, by which
        // time any line this request could have produced would have been written.
        Assert.Equal(lines, await Observed.SettledCountAsync(Harness, Suite.Ct));
    }

    [Fact]
    public async Task A_listed_api_name_does_reach_its_upstream()
    {
        // The control. Every row above asserts that something did not reach an upstream, which a
        // gateway that could reach no upstream at all would also satisfy
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        using TestBrowser browser = Harness.CreateBrowser();
        await Harness.SignInAsync(browser, "app1", Suite.Ct);

        int upstreams = Harness.Echo.Requests.Count;
        BrowserResponse response = await browser.GetAsync("/api/echo/thing", ct: Suite.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(upstreams + 1, Harness.Echo.Requests.Count);

        // The prefix is removed, as the upstream contract says: the upstream sees /thing.
        Assert.Equal("/thing", Harness.Echo.Requests[^1].Path);
    }

    [Fact]
    public async Task A_signed_in_caller_meets_the_same_refusals_and_no_upstream_sees_the_request()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        HarnessApp app = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        string cookie = SignedIn.SessionCookieName + "=" + SignedIn.SessionCookie(browser, app);

        // Control first: the session reaches a listed upstream.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/echo/thing", ct: Suite.Ct)).StatusCode);
        int upstreams = harness.Echo.Requests.Count;

        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse unlisted = await browser.GetAsync("/api/nosuch/thing", ct: Suite.Ct);
        Contract.ErrorRow unknown = Contract.Error("The app lists no upstream under that name");
        Assert.Equal(unknown.Status, (int)unlisted.StatusCode);
        Assert.Contains($"\"code\":\"{unknown.Code}\"", unlisted.Body, StringComparison.Ordinal);
        Assert.Equal(RuleIds.ApiUnknown, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));

        Contract.ErrorRow invalid = Contract.Error("The path failed normalisation");
        foreach (string target in Escapes)
        {
            before = await Observed.SettledCountAsync(harness, Suite.Ct);
            RawResponse escaped = await RawHttp.SendAsync(
                harness.GatewayAddress, "GET", target, app.Host,
                [("Sec-Fetch-Site", FetchMetadata.SameOrigin), ("Cookie", cookie)], Suite.Ct);

            Assert.Equal(invalid.Status, escaped.StatusCode);
            Assert.Equal(RuleIds.PathInvalid, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
        }

        Assert.Equal(upstreams, harness.Echo.Requests.Count);
    }

    /// <summary>
    /// Neither upstream saw the request: the echo upstream recorded nothing new, and the body is the
    /// gateway's own problem document rather than the mock API's JSON, which is what would have come
    /// back had a listener answered.
    /// </summary>
    private void AssertNoUpstreamSaw(string body, int upstreamsBefore)
    {
        Assert.Equal(upstreamsBefore, Harness.Echo.Requests.Count);
        Assert.DoesNotContain("\"api\":", body, StringComparison.Ordinal);
    }
}
