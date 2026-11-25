using System.Net;
using System.Text.Json;
using Gateway.IntegrationTests.Functional;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using OpenTelemetry.Metrics;
using Platform.Abstractions;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// <c>fragment_id</c> in the request telemetry, and N-16 over a fragment journey, on the composed gateway
/// (#1007, part of #961; contracts/telemetry.md). <c>tests/Platform.Telemetry.Tests/FragmentIdTests.cs</c>
/// proves the stage against a stand-in for host resolution; here the real host resolution pins the
/// <see cref="IFragmentFeature"/> the field is read from.
/// </summary>
public sealed class FragmentTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>fragment_id</c> is on the request line, <c>gateway.requests</c> and <c>security.rejections</c> for
    /// a request under a resolved fragment, and absent for a shell request, an unknown fragment id and a
    /// fragment-less host.
    /// </summary>
    [Fact]
    public async Task Fragment_id_is_on_the_line_and_both_counts_only_for_a_request_under_a_resolved_fragment()
    {
        // Mutation that turns it red: taking fragment_id from the path's first segment under /fragments/
        // rather than the pinned feature puts "nope" and "x" on lines 4 and 5 and on their points; leaving
        // it off security.rejections fails the rejection point.
        CapturedTelemetry telemetry = new();
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            telemetry.Options(GatewayHarnessOptions.Shell(1) with { Apps = ["shell", "app1"] }), Ct);
        HarnessApp shell = harness.App("shell");
        HarnessApp app1 = harness.App("app1");
        using TestBrowser browser = harness.CreateBrowser();
        await Fragments.LoginAsync(browser, shell, "/", Ct);
        await harness.SignInAsync(browser, "app1", Ct);
        await browser.NavigateAsync(shell.Url("/"), ct: Ct);

        JsonElement entry = await LineOfAsync(harness, () => browser.GetAsync(shell.Url("/fragments/orders/entry.js").ToString(), ct: Ct));
        JsonElement api = await LineOfAsync(harness, () => browser.GetAsync(shell.Url("/fragments/orders/api/echo/items/1").ToString(), ct: Ct));
        JsonElement document = await LineOfAsync(harness, () => browser.GetAsync(shell.Url("/").ToString(), ct: Ct));
        JsonElement unknown = await LineOfAsync(harness, () => browser.GetAsync(shell.Url("/fragments/nope/a.js").ToString(), ct: Ct));
        JsonElement reserved = await LineOfAsync(harness, () => browser.GetAsync(app1.Url("/fragments/x/a.js").ToString(), ct: Ct));

        // A rejection under the resolved fragment: an unsafe call with no antiforgery token.
        JsonElement rejected = await LineOfAsync(harness, () => browser.FetchAsync(
            HttpMethod.Post, shell.Url("/fragments/orders/api/echo/items/2").ToString(), new StringContent("{}"), ct: Ct));

        Assert.Equal("orders", entry.GetProperty("fragment_id").GetString());
        Assert.Equal("orders", api.GetProperty("fragment_id").GetString());
        Assert.Equal("orders", rejected.GetProperty("fragment_id").GetString());
        string rule = rejected.GetProperty("rule").GetString() ?? throw new InvalidOperationException($"the unsafe call was not rejected: {rejected}");
        foreach (JsonElement line in (JsonElement[])[document, unknown, reserved])
        {
            Assert.False(line.TryGetProperty("fragment_id", out _), line.GetRawText());
        }

        Assert.Equal("path.reserved", reserved.GetProperty("rule").GetString());

        // The counts. The meter listener is process-wide, so other harnesses' points are here too: each
        // check reads this test's points by its app ids and the values only this test's requests make.
        telemetry.Flush(harness);
        List<Dictionary<string, object?>> requests = Points(telemetry, "gateway.requests");
        List<Dictionary<string, object?>> rejections = Points(telemetry, "security.rejections");
        Assert.Contains(requests, tags => Equals(tags["app_id"], "shell") && Equals(tags.GetValueOrDefault("fragment_id"), "orders"));
        Assert.Contains(requests, tags => Equals(tags["app_id"], "shell") && !tags.ContainsKey("fragment_id"));
        Assert.Contains(rejections, tags => Equals(tags["rule"], rule) && Equals(tags["app_id"], "shell") && Equals(tags.GetValueOrDefault("fragment_id"), "orders"));
        Assert.Contains(rejections, tags => Equals(tags["rule"], "path.reserved") && Equals(tags["app_id"], "app1") && !tags.ContainsKey("fragment_id"));
        Assert.All(requests.Concat(rejections).Where(tags => tags.ContainsKey("fragment_id")),
            tags => Assert.Contains((string?)tags["fragment_id"], GatewayHarnessOptions.ShellFragmentIds));
    }

    /// <summary>
    /// N-16 over a fragment journey: a chain login at N = 2, fragment API calls that refresh both
    /// fragments' sets, and a logout that revokes all three. No fragment's or shell's client secret, no
    /// access, refresh or ID token, no authorization code, state or continuation, and no session key
    /// appears in any line the gateway logged.
    /// </summary>
    [Fact]
    public async Task N16_no_credential_of_a_fragment_journey_reaches_any_log_line()
    {
        // Mutation that turns it red: logging the chain step's query string (the continuation) or a
        // revocation line naming the token it revoked puts a value below in a line.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.Shell(2) with { KeyRing = KeyRingSharing.OneParameterStore }, Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        BrowserResponse landed = await Fragments.LoginAsync(browser, shell, "/", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        string key = Assert.Single(harness.SessionStore!.Created);
        string idToken = Records.ShellToken((await harness.SessionStore.GetAsync(key, Ct))!, "id_token")!;

        foreach (HarnessFragment fragment in shell.Fragments)
        {
            Assert.Equal(fragment.ClientId, await Fragments.ClientIdAtEchoAsync(harness, browser, shell, fragment.FragmentId, Ct));
        }

        Assert.Equal(2, harness.Idp.RefreshGrants.Count(g => g.Ok));
        BrowserResponse logout = await browser.SubmitFormAsync("/bff/logout", options: Steps.Manual, ct: Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(3, harness.Idp.Revocations.Count);

        // What the browser carried in its URLs: each code, state and continuation the journey passed.
        string[] carried = [.. landed.RedirectedFrom
            .SelectMany(url => QueryHelpers.ParseQuery(url.Query))
            .Where(pair => pair.Key is "code" or "state" or "c")
            .Select(pair => pair.Value.ToString())];
        Assert.Contains(landed.RedirectedFrom, url => QueryHelpers.ParseQuery(url.Query).ContainsKey("c"));
        Assert.Equal(3, landed.RedirectedFrom.Count(url => QueryHelpers.ParseQuery(url.Query).ContainsKey("code")));

        string[] values =
        [
            .. harness.Apps.Values.Select(app => app.ClientSecret),
            .. harness.Apps.Values.SelectMany(app => app.Fragments).Select(fragment => fragment.ClientSecret),
            .. harness.Idp.IssuedAccessTokens,
            .. harness.Idp.IssuedRefreshTokens,
            idToken,
            key,
            .. carried,
        ];
        Assert.All(values, value => Assert.False(string.IsNullOrEmpty(value)));
        Assert.True(values.Length >= 2 + 2 + 6 + 6 + 1 + 1 + 3, $"only {values.Length} values to search for.");

        string[] lines = [.. harness.Log.Rendered, .. harness.Log.Lines.Select(line => line.Text)];
        Assert.NotEmpty(lines);
        List<string> leaks = [];
        foreach (string line in lines)
        {
            // Named by kind and position, never by value: a test's output is a log too.
            leaks.AddRange(values.Select((value, index) => (value, index))
                .Where(pair => line.Contains(pair.value, StringComparison.Ordinal))
                .Select(pair => $"value #{pair.index} at {line.IndexOf(pair.value, StringComparison.Ordinal)} of a {line.Length}-character line"));
        }

        Assert.Empty(leaks);

        // The search can fail: the same values in a line made to hold one are found.
        Assert.Contains(values, value => ("request " + values[^1] + " seen").Contains(value, StringComparison.Ordinal));
    }

    /// <summary>Sends one request and returns the request line it produced, the last in the harness's log.</summary>
    private static async Task<JsonElement> LineOfAsync(GatewayHarness harness, Func<Task<BrowserResponse>> send)
    {
        int before = RequestLines(harness).Count;
        await send();
        IReadOnlyList<JsonElement> lines = RequestLines(harness);
        Assert.Equal(before + 1, lines.Count);
        return lines[^1];
    }

    private static IReadOnlyList<JsonElement> RequestLines(GatewayHarness harness) =>
        [.. harness.Log.Json.Where(line => line.GetProperty("msg").GetString() == "request")];

    private static List<Dictionary<string, object?>> Points(CapturedTelemetry telemetry, string name)
    {
        List<Dictionary<string, object?>> points = [];
        foreach (Metric metric in telemetry.Metrics.Where(m => m.Name == name && m.MeterName == "Platform.Telemetry"))
        {
            foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
            {
                Dictionary<string, object?> tags = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> tag in point.Tags)
                {
                    tags[tag.Key] = tag.Value;
                }

                points.Add(tags);
            }
        }

        return points;
    }
}
