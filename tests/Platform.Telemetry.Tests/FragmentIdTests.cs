using System.Text.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Telemetry.Tests;

/// <summary>
/// <c>fragment_id</c> on the request line and as a tag of <c>gateway.requests</c> and
/// <c>security.rejections</c>, read from the <see cref="IFragmentFeature"/> host resolution pins
/// (contracts/telemetry.md, under change request #986 of the fragments spec #961; #1007). The value is
/// the pinned fragment's id and never a path segment: a shell's request and a request whose
/// <c>fragmentId</c> names no fragment of the app carry no field and no tag. Here the host-resolution
/// stand-in in <see cref="TelemetryHost"/> pins the feature as the real stage does; the composed proof
/// is in <c>tests/Gateway.IntegrationTests/Fragments/FragmentTelemetryTests.cs</c>.
/// </summary>
public sealed class FragmentIdTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FragmentDefinition[] Fragments(string appId) =>
        [TestApps.Fragment("orders", appId), TestApps.Fragment("reports", appId)];

    private static async Task SendAsync(HttpClient client, TelemetryHost host, string path, string? reject = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(host.BaseAddress, path));
        if (reject is not null)
        {
            request.Headers.Add(TelemetryHost.RejectHeader, reject);
        }

        using HttpResponseMessage response = await client.SendAsync(request, Ct);
    }

    private static IReadOnlyList<JsonElement> RequestLines(TelemetryHost host) =>
        [.. host.Log.Json.Where(line => line.GetProperty("msg").GetString() == "request")];

    [Fact]
    public async Task A_request_under_a_resolved_fragment_logs_its_fragment_id_after_app_id()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, fragments: Fragments(TelemetryHost.AppId));
        using HttpClient client = new();

        await SendAsync(client, host, "/fragments/reports/entry.js");

        // Mutation that turns it red: leaving fragment_id out of RequestLine's fields fails here with
        // "The given key was not present".
        JsonElement line = Assert.Single(RequestLines(host));
        Assert.Equal("reports", line.GetProperty("fragment_id").GetString());

        // Where the "Log fields" row of contracts/telemetry.md puts it: after app_id, before
        // registry_rev. Mutation: appending fragment_id after duration_ms fails this.
        string[] names = [.. line.EnumerateObject().Select(field => field.Name)];
        Assert.Equal(Array.IndexOf(names, "app_id") + 1, Array.IndexOf(names, "fragment_id"));
        Assert.Equal(Array.IndexOf(names, "fragment_id") + 1, Array.IndexOf(names, "registry_rev"));
    }

    [Fact]
    public async Task A_shell_request_and_an_unknown_fragment_id_log_no_fragment_id_field()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, fragments: Fragments(TelemetryHost.AppId));
        using HttpClient client = new();

        // In order: a fragment's request, so the feature has been pinned on this connection once; the
        // shell's; an id the entry does not list, whose segment must not reach the line; and the shell's
        // API, where /fragments/ is not the prefix.
        await SendAsync(client, host, "/fragments/orders/entry.js");
        await SendAsync(client, host, "/index.html");
        await SendAsync(client, host, "/fragments/canary-not-a-fragment/entry.js");
        await SendAsync(client, host, "/api/orders/items");

        // Mutation that turns it red: writing fragment_id unconditionally, as rule and session_ref are,
        // puts a null on the last three lines; taking it from the path's segment rather than the
        // feature puts "canary-not-a-fragment" on the third.
        IReadOnlyList<JsonElement> lines = RequestLines(host);
        Assert.Equal(4, lines.Count);
        Assert.Equal("orders", lines[0].GetProperty("fragment_id").GetString());
        foreach (JsonElement line in lines.Skip(1))
        {
            Assert.False(line.TryGetProperty("fragment_id", out _), line.GetRawText());
        }

        Assert.DoesNotContain(host.Log.Rendered, rendered => rendered.Contains("canary-not-a-fragment\"", StringComparison.Ordinal)
            && rendered.Contains("fragment_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gateway_requests_is_tagged_fragment_id_only_for_a_request_under_a_resolved_fragment()
    {
        // An app id no other test resolves: the meter listener is process-wide.
        const string AppId = "app-fragment-requests";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, appId: AppId, fragments: Fragments(AppId));
        using HttpClient client = new();

        await SendAsync(client, host, "/fragments/orders/entry.js");
        await SendAsync(client, host, "/fragments/orders/assets/a.js");
        await SendAsync(client, host, "/index.html");
        await SendAsync(client, host, "/fragments/unknown/entry.js");

        List<(long Count, Dictionary<string, object?> Tags)> points =
            [.. TraceTests.PointsOf(host, "gateway.requests").Where(point => Equals(point.Tags["app_id"], AppId))];

        // Mutation that turns it red: leaving the tag off gateway.requests folds the two fragment
        // requests into the asset point with the other two, so there is no point tagged "orders".
        (long fragmentCount, _) = Assert.Single(points, point => Equals(point.Tags.GetValueOrDefault("fragment_id"), "orders"));
        Assert.Equal(2, fragmentCount);

        // Mutation: tagging every request (a null fragment_id included) puts the tag on these points too.
        (long otherCount, Dictionary<string, object?> other) = Assert.Single(points, point => !Equals(point.Tags.GetValueOrDefault("fragment_id"), "orders"));
        Assert.Equal(2, otherCount);
        Assert.False(other.ContainsKey("fragment_id"));
    }

    [Fact]
    public async Task Security_rejections_is_tagged_fragment_id_only_for_a_rejection_under_a_resolved_fragment()
    {
        const string AppId = "app-fragment-rejections";
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, appId: AppId, fragments: Fragments(AppId));
        using HttpClient client = new();

        await SendAsync(client, host, "/fragments/reports/api/reports/x", reject: "test.fragment-rejected");
        await SendAsync(client, host, "/api/reports/x", reject: "test.fragment-rejected");

        List<(long Count, Dictionary<string, object?> Tags)> points =
            [.. TraceTests.PointsOf(host, "security.rejections").Where(point => Equals(point.Tags["rule"], "test.fragment-rejected"))];

        // Mutation that turns it red: leaving the tag off security.rejections makes one point of two.
        Assert.Equal(2, points.Count);
        Dictionary<string, object?> fragment = Assert.Single(points, point => point.Tags.ContainsKey("fragment_id")).Tags;
        Assert.Equal("reports", fragment["fragment_id"]);
        Assert.Equal(AppId, fragment["app_id"]);

        // The shell's rejection keeps the two tags it had before fragments: rule and app_id.
        Dictionary<string, object?> shell = Assert.Single(points, point => !point.Tags.ContainsKey("fragment_id")).Tags;
        Assert.Equal(2, shell.Count);
        Assert.All(points, point => Assert.Equal(1, point.Count));
    }
}
