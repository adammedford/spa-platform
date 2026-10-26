using System.Text.Json;
using OpenTelemetry.Metrics;

namespace Platform.Telemetry.Tests;

/// <summary>
/// <c>read_refused</c> on the request line and the <c>storage.read_refusals</c> count, both from
/// <c>IRequestTelemetryFeature.ReadRefused</c> (contracts/telemetry.md; change request
/// contracts/changes/issue-381-object-read-refusal.md, items 5 and 6). The asset route in
/// <c>Platform.Hosting</c> writes the field; <c>UsePlatformTelemetry</c> logs it and counts it, so one
/// owner makes one emission decision per request. Here a later-stage stand-in writes it
/// (<see cref="TelemetryHost.ReadRefusedHeader"/>), and the count is read through an in-memory metric
/// exporter with Information logging on, which is the hermetic tier of the change request's evidence.
/// </summary>
public sealed class ReadRefusalTests
{
    private const string Counter = "storage.read_refusals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SendAsync(HttpClient client, TelemetryHost host, string path, string? refused)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, path));
        if (refused is not null)
        {
            request.Headers.Add(TelemetryHost.ReadRefusedHeader, refused);
        }

        using HttpResponseMessage response = await client.SendAsync(request, Ct);
    }

    private static IReadOnlyList<JsonElement> RequestLines(TelemetryHost host) =>
        [.. host.Log.Json.Where(line => line.GetProperty("msg").GetString() == "request")];

    /// <summary>
    /// This host's points of the counter. The meter listener is process-wide, so every host in the test
    /// process reports here; each test runs under an app id no other test resolves, and reads its points
    /// by it.
    /// </summary>
    private static List<(long Count, Dictionary<string, object?> Tags)> PointsFor(TelemetryHost host, string appId) =>
        [.. TraceTests.PointsOf(host, Counter).Where(point => Equals(point.Tags["app_id"], appId))];

    [Fact]
    public async Task A_refused_read_puts_its_key_class_on_the_request_line_beside_the_other_feature_fields()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        await SendAsync(client, host, "/assets/gone.0badf00d.js", "asset");

        // Mutation that turns it red: leaving read_refused out of RequestLine's fields fails the first
        // assertion with "The given key was not present".
        JsonElement line = Assert.Single(RequestLines(host));
        Assert.Equal("asset", line.GetProperty("read_refused").GetString());
        Assert.Equal(404, line.GetProperty("status").GetInt32());

        // Beside rule and session_ref, the feature's other two fields, so the line keeps the feature's
        // fields together. Mutation: appending read_refused after duration_ms fails this.
        string[] names = [.. line.EnumerateObject().Select(field => field.Name)];
        Assert.Equal(Array.IndexOf(names, "session_ref") + 1, Array.IndexOf(names, "read_refused"));
    }

    [Fact]
    public async Task A_request_with_no_refusal_logs_no_read_refused_field_even_after_one_that_had_one()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct);
        using HttpClient client = new();

        await SendAsync(client, host, "/assets/gone.0badf00d.js", "shell");
        await SendAsync(client, host, "/index.html", refused: null);

        // The field is written only when the read was refused (issue #429: "omits the field when it is
        // null"), and the feature is fresh per request, so the second line on the same connection has
        // none. Mutation that turns it red: writing read_refused unconditionally, as rule and session_ref
        // are, puts a null on the second line.
        IReadOnlyList<JsonElement> lines = RequestLines(host);
        Assert.Equal(2, lines.Count);
        Assert.Equal("shell", Assert.Single(lines, l => l.GetProperty("status").GetInt32() == 404).GetProperty("read_refused").GetString());
        JsonElement plain = Assert.Single(lines, l => l.GetProperty("status").GetInt32() == 200);
        Assert.False(plain.TryGetProperty("read_refused", out _), "a request whose read was not refused carries read_refused.");
    }

    [Fact]
    public async Task Refusals_are_counted_by_app_and_key_class_once_for_each_line_that_carries_the_field()
    {
        const string app = "app-refusals-counted";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, appId: app);
        using HttpClient client = new();

        await SendAsync(client, host, "/assets/a.js", "asset");
        await SendAsync(client, host, "/assets/b.js", "asset");
        await SendAsync(client, host, "/", "shell");
        await SendAsync(client, host, "/index.html", refused: null);
        await SendAsync(client, host, "/assets/c.js", refused: null);

        List<(long Count, Dictionary<string, object?> Tags)> points = PointsFor(host, app);

        // Mutations that turn it red: counting every request (no null check) adds a point with a null
        // key_class; dropping the key_class tag merges the two points into one of 3; tagging the route
        // instead of the class leaves no point whose key_class is "asset".
        (long assets, Dictionary<string, object?> assetTags) = Assert.Single(points, p => Equals(p.Tags["key_class"], "asset"));
        (long shells, _) = Assert.Single(points, p => Equals(p.Tags["key_class"], "shell"));
        Assert.Equal(2, points.Count);
        Assert.Equal(2, assets);
        Assert.Equal(1, shells);
        Assert.Equal(["app_id", "key_class"], assetTags.Keys.Order(StringComparer.Ordinal));

        // The count equals the lines that carry the field, which is the one emission decision per
        // request the single owner guarantees (contracts/telemetry.md -> The instruments).
        int carrying = RequestLines(host).Count(line => line.TryGetProperty("read_refused", out _));
        Assert.Equal(3, carrying);
        Assert.Equal(carrying, points.Sum(p => p.Count));
    }

    [Fact]
    public async Task The_count_is_added_before_the_request_lines_level_check()
    {
        // The request line's category held above Information: no line is written, and the refusal is
        // still counted, as security.rejections is (contracts/telemetry.md -> The instruments).
        const string app = "app-refusals-unlogged";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, appId: app, configuration: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:" + RequestTelemetry.Category] = "Warning",
        });
        using HttpClient client = new();

        await SendAsync(client, host, "/assets/a.js", "asset");

        // Mutation that turns it red: moving the Add inside the IsEnabled(Information) block leaves no
        // point for this app.
        Assert.Empty(RequestLines(host));
        (long count, _) = Assert.Single(PointsFor(host, app));
        Assert.Equal(1, count);
    }
}
