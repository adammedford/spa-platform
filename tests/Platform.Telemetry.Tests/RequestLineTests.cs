using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Platform.Abstractions;

namespace Platform.Telemetry.Tests;

/// <summary>
/// <c>UsePlatformTelemetry</c>'s request log line (contracts/pipeline.md, contracts/telemetry.md): one JSON line per
/// request, carrying the contract's fields and never the query string.
/// </summary>
public sealed class RequestLineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement RequestLine(TelemetryHost host) =>
        Assert.Single(host.Log.Json, line => line.GetProperty("msg").GetString() == "request");

    [Fact]
    public async Task One_request_writes_one_json_line_with_the_contract_fields()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        await client.GetAsync(new Uri(host.BaseAddress, "/index.html"), Ct);

        JsonElement line = RequestLine(host);
        Assert.Equal("asset", line.GetProperty("route").GetString());
        Assert.Equal(200, line.GetProperty("status").GetInt32());
        Assert.Equal("app1", line.GetProperty("app_id").GetString());
        Assert.Equal(1, line.GetProperty("registry_rev").GetInt64());
        Assert.Equal("test-cell", line.GetProperty("cell").GetString());
        Assert.Equal("information", line.GetProperty("level").GetString());
        Assert.True(line.GetProperty("duration_ms").GetDouble() >= 0);
        Assert.True(DateTimeOffset.TryParse(line.GetProperty("ts").GetString(), out _));
        Assert.Matches("^[0-9a-f]{32}$", line.GetProperty("trace_id").GetString());
        Assert.Matches("^[0-9a-f]{16}$", line.GetProperty("span_id").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rule").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("session_ref").ValueKind);
    }

    [Fact]
    public async Task A_rejection_puts_the_rule_id_a_later_stage_wrote_on_the_line()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, "/bff/session"));
        request.Headers.Add(TelemetryHost.RejectHeader, "gate.origin");

        await client.SendAsync(request, Ct);

        JsonElement line = RequestLine(host);
        Assert.Equal("gate.origin", line.GetProperty("rule").GetString());
        Assert.Equal(403, line.GetProperty("status").GetInt32());
        Assert.Equal("app1", line.GetProperty("app_id").GetString());
    }

    [Fact]
    public async Task A_session_puts_the_reference_computed_by_the_later_stage_on_the_line()
    {
        const string key = "session-key-4b1e9c";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new(new HttpClientHandler { UseCookies = false });
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, "/index.html"));
        request.Headers.Add("Cookie", TelemetryHost.SessionCookie + "=" + key);

        await client.SendAsync(request, Ct);

        JsonElement line = RequestLine(host);
        Assert.Equal(SessionRef.Of(key), line.GetProperty("session_ref").GetString());
        Assert.Matches("^[0-9a-f]{12}$", line.GetProperty("session_ref").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("rule").ValueKind);
        // N-16 in LogCanaryTests supplies the logging control that proves session-key redaction.
    }

    [Fact]
    public async Task Each_request_starts_with_no_rule_and_no_session()
    {
        // The feature is per request: a rejection or a session on one request is not carried into
        // the next on the same connection.
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new(new HttpClientHandler { UseCookies = false });
        using HttpRequestMessage first = new(HttpMethod.Get, new Uri(host.BaseAddress, "/index.html"));
        first.Headers.Add(TelemetryHost.RejectHeader, "csrf.token");
        first.Headers.Add("Cookie", TelemetryHost.SessionCookie + "=first-key");
        await client.SendAsync(first, Ct);
        JsonElement rejected = RequestLine(host);
        Assert.Equal(403, rejected.GetProperty("status").GetInt32());
        Assert.Equal("csrf.token", rejected.GetProperty("rule").GetString());
        Assert.Equal(SessionRef.Of("first-key"), rejected.GetProperty("session_ref").GetString());

        await client.GetAsync(new Uri(host.BaseAddress, "/status/204"), Ct);

        JsonElement second = Assert.Single(host.Log.Json, l =>
            l.GetProperty("msg").GetString() == "request" && l.GetProperty("status").GetInt32() == 204);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("rule").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("session_ref").ValueKind);
    }

    [Theory]
    [InlineData("/api/orders/items", "api")]
    [InlineData("/bff/session", "bff")]
    [InlineData("/signout-callback-oidc", "callback")]
    [InlineData("/healthz", "health")]
    [InlineData("/readyz", "health")]
    [InlineData("/", "asset")]
    [InlineData("/apiary/index.html", "asset")]
    public async Task The_route_names_which_part_of_the_contract_the_path_belongs_to(string path, string route)
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        await client.GetAsync(new Uri(host.BaseAddress, path), Ct);

        Assert.Equal(route, RequestLine(host).GetProperty("route").GetString());
    }

    [Fact]
    public async Task An_unknown_host_is_logged_with_no_app()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, "/"));
        request.Headers.Add("X-Test-Unknown-Host", "1");

        await client.SendAsync(request, Ct);

        JsonElement line = RequestLine(host);
        Assert.Equal(404, line.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("app_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("registry_rev").ValueKind);
        Assert.Equal("host.unknown", line.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task N16_vendor_error_fields_are_dropped_even_when_configuration_reenables_the_categories()
    {
        const string library = "Duende.AccessTokenManagement.OpenIdConnect.Internal.Endpoint";
        const string authentication = "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler";
        const string canary = "CANARY-PROVIDER-REFRESH-TOKEN";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:" + library] = "Trace",
            ["Logging:LogLevel:" + authentication] = "Trace",
        });
        ILoggerFactory logs = host.Services.GetRequiredService<ILoggerFactory>();
        logs.CreateLogger(library).LogError("Error refreshing access token. Error = {Error}", canary);
        logs.CreateLogger(authentication).LogWarning("Remote error: {ErrorDescription}", canary);
        logs.CreateLogger("Platform.Identity.AuthenticationEvents").LogWarning("Safe callback outcome {Rule}", "auth.callback");

        Assert.DoesNotContain(host.Log.Rendered, line => line.Contains(canary, StringComparison.Ordinal));
        Assert.Contains(host.Log.Json, line => line.GetProperty("msg").GetString() == "Safe callback outcome auth.callback");
    }

    [Fact]
    public async Task The_framework_request_log_is_replaced()
    {
        // The framework's "Request starting" and "Request finished" lines carry the query string
        // (contracts/telemetry.md -> Why query strings are dropped). Configuration asks for them at
        // Information, which is the framework's default, and they still do not appear.
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
        });
        using HttpClient client = new();

        await client.GetAsync(new Uri(host.BaseAddress, "/index.html?q=1"), Ct);

        Assert.DoesNotContain(host.Log.Json, line =>
            line.GetProperty("category").GetString() == "Microsoft.AspNetCore.Hosting.Diagnostics");
        Assert.DoesNotContain(host.Log.Rendered, line => line.Contains("q=1", StringComparison.Ordinal));
    }
}
