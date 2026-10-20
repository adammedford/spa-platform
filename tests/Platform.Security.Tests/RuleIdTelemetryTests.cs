using System.Net;

namespace Platform.Security.Tests;

/// <summary>
/// Task 4.3a: every rule id this project's stages refuse under reaches
/// <c>IRequestTelemetryFeature.Rule</c>, where <c>UsePlatformTelemetry</c> reads it for the one request
/// log line and for <c>security.rejections</c> (<c>contracts/telemetry.md</c> -> "How <c>rule</c> and
/// <c>session_ref</c> reach the request line"). The ids are <c>contracts/request-gate.md</c> -> Rule
/// ids.
///
/// <c>UsePlatformTelemetry</c> belongs to workstream 6; these component hosts install
/// <c>Platform.Testing</c>'s <see cref="Platform.Testing.SettableRequestTelemetryFeature"/> in its
/// place, which is what a stage's own test project can do. The stages end through
/// <c>Rejection.EndAsync</c> and <c>Rejection.EndMethodAsync</c>, whose null-conditional write with no
/// feature installed is proven once in tests/Platform.Abstractions.Tests (#142, Q10); what is proven
/// here is the wiring, status and rule together.
///
/// Each assertion names the id it expects, so a stage that set a different one, or none, fails it.
/// It also asserts the stage's own Warning line carries the same id: both come from one value in the
/// stage, and a drift between them would show here.
///
/// <para>The method allowlist (<c>MethodAllowlist</c>, inside <c>UsePlatformForwardedHeaders</c>) is
/// here too. <c>UsePlatformTelemetry</c> runs before it (ADR-0018), so its 405 sets <c>gate.method</c>
/// like every other rejection, and the stand-in in <see cref="GateHost"/> sits in the same
/// place.</para>
/// </summary>
public sealed class RuleIdTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string, string?) FetchSite(string? value) => ("Sec-Fetch-Site", value);

    private static (string, string?) Origin(string value) => ("Origin", value);

    /// <summary>
    /// One request was made, it was refused with <paramref name="status"/>, and it left
    /// <paramref name="rule"/> on the feature and on the stage's own Warning line.
    /// </summary>
    private static void AssertRule(GateHost host, HttpResponseMessage response, HttpStatusCode status, string rule)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(rule, Assert.Single(host.TelemetryRules));
        Assert.Single(host.LinesFor(rule));
        Assert.Equal(0, host.Reached);
    }

    // ---- UsePlatformRequestGate: contracts/request-gate.md ----

    [Fact]
    public async Task The_gate_sets_path_invalid()
    {
        // Over a socket, because Uri resolves dot segments before the request leaves HttpClient.
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        string response = await host.RawAsync("GET /api/orders/../secret HTTP/1.1", Ct, "Sec-Fetch-Site: same-origin\r\n");

        Assert.StartsWith("HTTP/1.1 400 ", response);
        Assert.Equal("path.invalid", Assert.Single(host.TelemetryRules));
        Assert.Single(host.LinesFor("path.invalid"));
        Assert.Equal(0, host.Reached);
    }

    [Fact]
    public async Task The_gate_sets_gate_cookie_name()
    {
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("GET", "/", Ct, ("Cookie", "__host-spa-session=planted"));

        AssertRule(host, response, HttpStatusCode.BadRequest, "gate.cookie-name");
    }

    [Fact]
    public async Task The_gate_sets_path_reserved()
    {
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("GET", "/signout-oidc", Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin));

        AssertRule(host, response, HttpStatusCode.NotFound, "path.reserved");
    }

    [Fact]
    public async Task The_gate_sets_gate_fetch_site()
    {
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("GET", "/api/orders/items", Ct, FetchSite("cross-site"));

        AssertRule(host, response, HttpStatusCode.Forbidden, "gate.fetch-site");
    }

    [Fact]
    public async Task The_gate_sets_gate_origin()
    {
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("POST", "/api/orders/items", Ct,
            FetchSite("same-origin"), Origin(GateHost.SiblingOrigin));

        AssertRule(host, response, HttpStatusCode.Forbidden, "gate.origin");
    }

    [Fact]
    public async Task The_gate_sets_gate_navigation()
    {
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("GET", "/bff/login", Ct, FetchSite("same-origin"));

        AssertRule(host, response, HttpStatusCode.Forbidden, "gate.navigation");
    }

    [Fact]
    public async Task The_gate_sets_gate_method()
    {
        // The request gate's own 405: the path takes a method it was not sent. The allowlist's 405 for
        // a method no path takes is The_method_allowlist_sets_gate_method, under the same id.
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync("POST", "/bff/session", Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin));

        AssertRule(host, response, HttpStatusCode.MethodNotAllowed, "gate.method");
    }

    // ---- UsePlatformForwardedHeaders: the method allowlist ----

    [Theory]
    [InlineData("TRACE")]
    [InlineData("PROPFIND")]
    public async Task The_method_allowlist_sets_gate_method(string method)
    {
        // A method no path accepts, refused by the allowlist inside UsePlatformForwardedHeaders before
        // the request gate sees it. UsePlatformTelemetry runs first (ADR-0018), so the feature is
        // there.
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        string response = await host.RawAsync($"{method} /api/orders/items HTTP/1.1", Ct, "Sec-Fetch-Site: same-origin\r\n");

        Assert.StartsWith("HTTP/1.1 405 ", response);
        Assert.Equal("gate.method", Assert.Single(host.TelemetryRules));
        Assert.Single(host.LinesFor("gate.method"));
        Assert.Equal(0, host.Reached);
    }

    [Theory]
    [InlineData("GET", "/api/orders/items")]
    [InlineData("GET", "/")]
    [InlineData("POST", "/bff/logout")]
    public async Task A_request_the_gate_passes_leaves_the_rule_null(string method, string path)
    {
        // The field says "rejected under this rule", so a request that was not rejected carries none:
        // this fails if a stage writes the id on its way past as well as on a refusal.
        await using GateHost host = await GateHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync(method, path, Ct,
            FetchSite("same-origin"), Origin(GateHost.AppOrigin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(host.TelemetryRules));
        Assert.Equal(1, host.Reached);
    }

    // ---- UsePlatformAntiforgery ----

    [Fact]
    public async Task Antiforgery_sets_csrf_token()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct, telemetry: true);

        using HttpResponseMessage response = await host.SendAsync(
            "POST", "/api/orders/items", AntiforgeryHost.App1, "alice", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf.token", Assert.Single(host.TelemetryRules));
        Assert.Single(host.LinesFor("csrf.token"));
        Assert.Equal(0, host.Reached);
    }

    [Fact]
    public async Task An_api_call_antiforgery_passes_leaves_the_rule_null()
    {
        await using AntiforgeryHost host = await AntiforgeryHost.StartAsync(Ct, telemetry: true);
        AntiforgeryHost.Issued issued = await host.IssueAsync(AntiforgeryHost.App1, "alice", Ct);

        using HttpResponseMessage response = await host.SendAsync(
            "POST", "/api/orders/items", AntiforgeryHost.App1, "alice", Ct,
            cookie: issued.Cookie, csrfHeader: issued.RequestToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Two requests: /bff/session issued the pair, and neither was refused.
        Assert.All(host.TelemetryRules, rule => Assert.Null(rule));
        Assert.Equal(2, host.TelemetryRules.Count);
    }
}
