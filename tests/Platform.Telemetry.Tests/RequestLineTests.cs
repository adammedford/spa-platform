using System.Text.Json;

namespace Platform.Telemetry.Tests;

/// <summary>
/// Stage 2's request log line (contracts/pipeline.md, contracts/telemetry.md): one JSON line per
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
        Assert.DoesNotContain(host.Log.Lines, line => line.Contains("q=1", StringComparison.Ordinal));
    }
}
