using System.Net;
using Microsoft.Extensions.Logging;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.3a: the one refusal this project answers, <c>404 unknown_api</c> from
/// <c>MapPlatformProxy</c>, leaves its rule id on <c>IRequestTelemetryFeature.Rule</c>, where
/// <c>UsePlatformTelemetry</c> reads it for the request log line and for <c>security.rejections</c>
/// (<c>contracts/telemetry.md</c> -> "How <c>rule</c> and <c>session_ref</c> reach the request line").
/// The id is <c>api.unknown</c> (<c>contracts/request-gate.md</c> -> Rule ids).
///
/// <c>UsePlatformTelemetry</c> belongs to workstream 6, so these component tests install
/// <c>Platform.Testing</c>'s <see cref="SettableRequestTelemetryFeature"/> in its place. The endpoint
/// ends through <c>Rejection.EndAsync</c>, whose null-conditional write with no feature installed is
/// proven once in tests/Platform.Abstractions.Tests (#142, Q10).
/// </summary>
public sealed class RuleIdTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unlisted_api_name_sets_api_unknown()
    {
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, telemetry: true, TestApps.Entry("app1"));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("api.unknown", Assert.Single(gateway.TelemetryRules));
    }

    [Fact]
    public async Task A_listed_api_leaves_the_rule_null()
    {
        // The field says "rejected under this rule", so a forwarded request carries none: this fails
        // if the stage writes the id on anything but its own refusal.
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, telemetry: true, TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/orders/items"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(gateway.TelemetryRules));
    }

    [Fact]
    public async Task An_unlisted_api_name_logs_its_rule_through_the_endpoint_s_template()
    {
        // The endpoint's Warning line is its own (the module never logs, #142 Q9), written through a
        // LoggerMessage template under the category it always had. A template's line carries an event
        // id named after its method; the ad hoc LogWarning call it replaced carried none, so this fails
        // if the endpoint goes back to one.
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, telemetry: true, TestApps.Entry("app1"));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        CapturedLine line = Assert.Single(gateway.Log.LinesFor("api.unknown"));
        Assert.Equal("Platform.Proxy.UnknownApi", line.Category);
        Assert.Equal(LogLevel.Warning, line.Level);

        // CapturedLine carries no event id (change request issue-145, section 4), so the template's is
        // read from the same line as the project's recorder kept it.
        LogLine recorded = Assert.Single(gateway.EventLines, recorded =>
            recorded.Category == line.Category && recorded.Message == line.Message);
        Assert.False(string.IsNullOrEmpty(recorded.EventId.Name), "the line was not written through a LoggerMessage template");

        // The path is attacker-chosen and stays out of the line.
        Assert.DoesNotContain("reports", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_captures_the_lines_below_information()
    {
        // A never-logged search reads every line the gateway wrote, Debug and Trace included (#140):
        // the framework's default floor is Information, so a host that leaves it there hides them.
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, TestApps.Entry("app1"));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(gateway.Log.Lines, line => line.Level < LogLevel.Information);
    }
}
