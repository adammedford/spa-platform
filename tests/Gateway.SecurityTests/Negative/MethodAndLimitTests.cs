using System.Text.Json;
using Gateway.TestHarness;
using Platform.Abstractions;

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
/// telemetry first. The rule id is asserted on the allowlist's own line, and the request line the
/// telemetry stage writes for the 405 is asserted to exist with its status. That line's
/// <c>rule</c> field is not read here yet: the allowlist's write of <c>gate.method</c> to
/// <c>IRequestTelemetryFeature</c> is workstream 4's (overnight ticket 63), and the assertion that
/// it reaches this line and <c>security.rejections</c> is ticket 64's, which waits on 63.</item>
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
/// </summary>
public sealed class MethodAndLimitTests(MethodAndLimitTests.Cell cell) : IClassFixture<MethodAndLimitTests.Cell>
{
    /// <summary>One harness for this class: these rows change nothing about it.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

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

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, method, target, Harness.App("app1").Host, ct: Suite.Ct);

        Contract.ErrorRow expected = Contract.Error("The path does not accept the method");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);

        // RFC 9110 section 15.5.6: a 405 carries Allow. The methods it names are the ones some path
        // of the gateway does accept, so neither of these is among them.
        string allow = Assert.Single(response.Values("Allow"));
        Assert.DoesNotContain(method, allow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GET", allow, StringComparison.Ordinal);

        // The rule id on the allowlist's own line.
        await Observed.UntilAsync(() => MethodRefusals().Count > warnings, "the stage's own refusal line", Suite.Ct);
        Assert.Equal(RuleIds.GateMethod, Observed.Field(MethodRefusals()[warnings], "rule"));

        // And the request line, now that the telemetry stage runs first (ADR-0018): exactly one,
        // carrying the status. Its rule id is ticket 63's to write and ticket 64's to assert.
        await Observed.UntilAsync(() => Observed.RequestLines(Harness).Count > before, "the request's own log line", Suite.Ct);
        IReadOnlyList<JsonElement> lines = Observed.RequestLines(Harness);
        Assert.Equal(before + 1, lines.Count);
        Assert.Equal((long?)expected.Status, Observed.Number(lines[before], "status"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task An_unsafe_method_on_an_asset_path_is_405_under_gate_method(string method)
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, method, "/index.html", Harness.App("app1").Host, ct: Suite.Ct);

        Contract.ErrorRow expected = Contract.Error("The path does not accept the method");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);

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

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/index.html", Harness.App("app1").Host, ct: Suite.Ct);

        Assert.Equal(200, response.StatusCode);
        Assert.Null(await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    [Fact]
    public async Task A_head_on_an_asset_path_is_accepted_and_carries_no_body()
    {
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "HEAD", "/index.html", Harness.App("app1").Host, ct: Suite.Ct);

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

        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", Harness.App("app1").Host,
            [("X-Too-Large", oversized)], Suite.Ct);

        Assert.Equal(431, response.StatusCode);
        Assert.Empty(response.Body);

        // And no stage logged any of it, because Kestrel answered before any stage ran. Swept over
        // the log rather than over the status line, which could not have carried a request header's
        // value in the first place (CODING_STANDARDS.md -> "An assertion must be able to fail").
        Assert.NotEmpty(Harness.Log.Lines);
        Assert.DoesNotContain(
            Harness.Log.Lines, line => line.Contains(oversized, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_header_inside_the_limit_is_served()
    {
        // The control: the 431 above is the limit being enforced, not the gateway refusing any
        // request that carries an unusual header.
        RawResponse response = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", "/", Harness.App("app1").Host,
            [("X-Small", new string('x', 512))], Suite.Ct);

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
