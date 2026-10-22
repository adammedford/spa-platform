using System.Text.Json;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-20, whole: "<c>TRACE</c>, <c>CONNECT</c>, an unsafe method on an asset path, oversized headers
/// -> 405 or 431." (docs/plan/04-tests.md.)
///
/// <para><b>Three different components answer these four, and the row is only proven if each is read
/// where it answers.</b></para>
/// <list type="bullet">
/// <item><c>TRACE</c> and <c>CONNECT</c> are refused by the method allowlist, which runs in
/// <c>UsePlatformForwardedHeaders</c>, behind <c>UsePlatformTelemetry</c> since ADR-0018 put
/// telemetry first. The rule id is asserted on the allowlist's own line, on the request line the
/// telemetry stage writes for the 405, and in the two counters that stage keeps:
/// <c>security.rejections</c> under <c>gate.method</c> with no app, because the allowlist answers
/// before host resolution pins one, and <c>gateway.requests</c>.</item>
/// <item>An unsafe method on an <b>asset path</b> is the request gate's per-path rule
/// (<c>UsePlatformRequestGate</c>), which runs after the telemetry stage like every other, so that
/// one does carry its rule id on the request line.</item>
/// <item><b>Oversized headers</b> are refused by <b>Kestrel</b>, before any middleware:
/// <c>contracts/http-contract.md</c> -> Errors says "Kestrel refuses oversized headers itself, with
/// 431 and no body". The limit itself is <c>PlatformSecurityLimits</c>, applied by
/// <c>AddPlatformSecurity</c>; this suite proves the composed gateway enforces it, which is only
/// visible because the harness runs the gateway on Kestrel rather than on a test server.</item>
/// </list>
///
/// <para><b>Why the raw socket.</b> No client library will send a <c>TRACE</c> or an HTTP/1.1
/// authority-form <c>CONNECT</c> to an origin server, and none will serialize a 40 KB header rather
/// than refusing it locally.</para>
///
/// <para><b>Every request here carries a session.</b> The class signs one browser in and sends its
/// cookies on each raw request, so the controls are served, as an asset is only served in a session
/// (ADR-0008), and each refusal differs from its control in the method or the header under test and
/// in nothing else.</para>
/// </summary>
public sealed class MethodAndLimitTests(MethodAndLimitTests.Cell cell) : IClassFixture<MethodAndLimitTests.Cell>
{
    /// <summary>One harness for this class, and one signed-in browser: these rows change nothing about either.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        /// <summary>The gateway's counters, which the request line alone cannot show were kept.</summary>
        public CountedMetrics Counts { get; private set; } = null!;

        /// <summary>The <c>Cookie</c> header of a browser signed in to app 1.</summary>
        public string Session { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Harness = await Suite.StartAsync(ct: ct);
            Counts = CountedMetrics.Watch(Harness);

            using TestBrowser browser = Harness.CreateBrowser();
            await Harness.SignInAsync(browser, "app1", ct);
            Session = SignedIn.CookieHeader(browser, Harness.App("app1"));
        }

        public async ValueTask DisposeAsync()
        {
            Counts.Dispose();
            await Harness.DisposeAsync();
        }
    }

    private GatewayHarness Harness => cell.Harness;

    private CountedMetrics Counts => cell.Counts;

    /// <summary>One request to app 1's host over the socket, carrying the class's session and any further headers.</summary>
    private Task<RawResponse> Send(string method, string target, params (string Name, string Value)[] headers) =>
        RawHttp.SendAsync(
            Harness.GatewayAddress, method, target, Harness.App("app1").Host,
            [("Cookie", cell.Session), .. headers], Suite.Ct);

    /// <summary>
    /// The methods no path of the gateway accepts, which the row names. <c>CONNECT</c> carries the
    /// authority form RFC 9110 section 9.3.6 gives it, read from <c>fixtures/apps/app1.json</c>
    /// rather than copied here, so a renamed fixture host cannot leave this sending a stale one.
    /// </summary>
    public static TheoryData<string, string> MethodsNoPathAccepts =>
        new()
        {
            { "TRACE", "/" },
            { "CONNECT", FixtureFiles.AppDefinition("app1")["host"]!.GetValue<string>() },
        };

    [Theory]
    [MemberData(nameof(MethodsNoPathAccepts))]
    public async Task A_method_no_path_accepts_is_405_under_gate_method_and_on_the_request_line(string method, string target)
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);
        int warnings = MethodRefusals().Count;

        // The counts are this gateway's own, and its other requests either name app1 (the request
        // gate's 405 on an asset path counts under the same rule with app1 named) or are not refused
        // under gate.method, so each difference below is this request's alone.
        long rejected = Rejections();
        long answered = Answered405s();

        RawResponse response = await Send(method, target);

        Contract.ErrorRow expected = Contract.Error("method_not_allowed");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.BodyText, StringComparison.Ordinal);

        // RFC 9110 section 15.5.6: a 405 carries Allow. The methods it names are the ones some path
        // of the gateway does accept, so neither of these is among them.
        string allow = Assert.Single(response.Values("Allow"));
        Assert.DoesNotContain(method, allow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GET", allow, StringComparison.Ordinal);

        // The rule id on the allowlist's own line.
        await Observed.UntilAsync(() => MethodRefusals().Count > warnings, "the stage's own refusal line", Suite.Ct);
        Assert.Equal(RuleIds.GateMethod, Observed.Field(MethodRefusals()[warnings], "rule"));

        // And the request line, now that the telemetry stage runs first (ADR-0018): exactly one,
        // carrying the status and the same rule id, with no app because none was resolved.
        await Observed.UntilAsync(() => Observed.RequestLines(Harness).Count > before, "the request's own log line", Suite.Ct);
        IReadOnlyList<JsonElement> lines = Observed.RequestLines(Harness);
        Assert.Equal(before + 1, lines.Count);
        Assert.Equal((long?)expected.Status, Observed.Number(lines[before], "status"));
        Assert.Equal(RuleIds.GateMethod, Observed.Field(lines[before], "rule"));
        Assert.Null(Observed.Field(lines[before], "app_id"));

        // The telemetry stage adds both counts before it writes the line, so the line being here
        // means the counts are too: one rejection under gate.method with no app, and one answered
        // request. N-20's composed half (contracts/telemetry.md -> The instruments).
        Assert.Equal(rejected + 1, Rejections());
        Assert.Equal(answered + 1, Answered405s());
    }

    /// <summary><c>security.rejections</c> under <c>gate.method</c> for no app.</summary>
    private long Rejections() =>
        Counts.Sum("security.rejections", ("rule", RuleIds.GateMethod), ("app_id", null));

    /// <summary><c>gateway.requests</c> answered 405 for no app.</summary>
    private long Answered405s() =>
        Counts.Sum("gateway.requests", ("status", Contract.Error("method_not_allowed").Status), ("app_id", null));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task An_unsafe_method_on_an_asset_path_is_405_under_gate_method(string method)
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await Send(method, "/index.html");

        Contract.ErrorRow expected = Contract.Error("method_not_allowed");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", response.BodyText, StringComparison.Ordinal);

        // An asset path takes GET and HEAD and nothing else (contracts/request-gate.md), and the
        // Allow header says so rather than naming every method some other path takes.
        Assert.Equal("GET, HEAD", Assert.Single(response.Values("Allow")));

        // This refusal is the request gate's, which writes the feature, so the id reaches the request line.
        Assert.Equal(RuleIds.GateMethod, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    [Fact]
    public async Task A_get_on_an_asset_path_is_not_refused_by_either_check()
    {
        // The control for both rows above: the allowlist and the per-path rule refuse methods, not
        // requests (CODING_STANDARDS.md -> "An assertion must be able to fail").
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await Send("GET", "/index.html");

        Assert.Equal(200, response.StatusCode);
        Assert.Null(await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    [Fact]
    public async Task A_head_on_an_asset_path_is_accepted_and_carries_no_body()
    {
        RawResponse response = await Send("HEAD", "/index.html");

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Headers_past_the_platforms_total_limit_are_431_with_no_body()
    {
        // 40 KB in one header. Two things pick the size, and neither is the limit itself: it is
        // above the 32 KB total that src/Platform.Security/PlatformSecurityLimits.cs sets on
        // Kestrel, and it is small enough that the whole request reaches the socket before the
        // server answers and closes. This row's claim is the 431, not the threshold — that the
        // limits are the platform's chosen values is proven in tests/Platform.Security.Tests, which
        // is where the constants live. If the platform's allowance ever grows past this, this test
        // reads a 200 and fails, which is the right way round.
        string oversized = new('x', 40 * 1024);

        RawResponse response = await Send("GET", "/", ("X-Too-Large", oversized));

        Assert.Equal(431, response.StatusCode);
        Assert.Empty(response.Body);

        // And no stage logged any of it, because Kestrel answered before any stage ran. Swept over
        // the log rather than over the status line, which could not have carried a request header's
        // value in the first place (CODING_STANDARDS.md -> "An assertion must be able to fail").
        Assert.NotEmpty(Harness.Log.Rendered);
        Assert.DoesNotContain(
            Harness.Log.Rendered, line => line.Contains(oversized, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_header_inside_the_limit_is_served()
    {
        // The control: the 431 above is the limit being enforced, not the gateway refusing any
        // request that carries an unusual header.
        RawResponse response = await Send("GET", "/", ("X-Small", new string('x', 512)));

        Assert.Equal(200, response.StatusCode);
    }

    /// <summary>
    /// The method allowlist's own refusal lines, picked out by the category the gateway logs them
    /// under. The stage is <c>internal</c> to another project, so the category is the one thing a
    /// test outside it can match on, and it is what the deployed log stream carries too.
    /// </summary>
    private IReadOnlyList<JsonElement> MethodRefusals() =>
        [.. Harness.Log.Json.Where(line =>
            Observed.Field(line, "category")?.EndsWith("MethodAllowlist", StringComparison.Ordinal) == true)];
}
