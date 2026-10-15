using System.Net;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// Task 4.3a: the one refusal this project answers, <c>404 unknown_api</c> at stage 10, leaves its
/// rule id on <c>IRequestTelemetryFeature.Rule</c>, where stage 2 reads it for the request log line
/// and for <c>security.rejections</c> (<c>contracts/telemetry.md</c> -> "How <c>rule</c> and
/// <c>session_ref</c> reach the request line"). The id is <c>api.unknown</c>
/// (<c>contracts/request-gate.md</c> -> Rule ids).
///
/// Stage 2 belongs to workstream 6, so these component tests install <c>Platform.Testing</c>'s
/// <see cref="SettableRequestTelemetryFeature"/> in its place. Every other test in this project
/// starts its host without one, which is what exercises the null-conditional write.
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
    public async Task An_unlisted_api_name_is_still_refused_with_no_telemetry_feature_installed()
    {
        // The write is null-conditional: this component host omits stage 2, so the refusal must
        // remain unchanged when no telemetry feature is installed (contracts/telemetry.md).
        await using ProxyHost gateway = await ProxyHost.StartAsync(Ct, TestApps.Entry("app1"));
        using HttpClient client = new();

        using HttpResponseMessage response = await client.SendAsync(
            gateway.Request(HttpMethod.Get, "app1.spa-poc.test", "/api/reports/items"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown_api", await Problem.CodeOf(response));
        Assert.Empty(gateway.TelemetryRules);
    }
}
