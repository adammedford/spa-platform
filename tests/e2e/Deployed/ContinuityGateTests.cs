using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The continuity class's raw calls meet the request gate on the local composed stack (#1001). The rows
/// themselves run on the two-task cell only, and the observer's tests run against recordings, which never
/// pass the gate; so on 2026-11-04 every raw call with a session was refused <c>gate.fetch-site</c> on the
/// cell and nothing had met the gate before (#348). Here the driver's own raw client
/// (<see cref="ContinuityDriver.RawOf"/>) sends each raw call shape of rows 3, 4, 6, 8 and 9 with a
/// signed-in session through the gateway, and no answer may be a <c>gate.*</c> rule's.
/// </summary>
public sealed class ContinuityGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_raw_call_shape_of_the_rows_passes_the_request_gate_with_a_signed_in_session()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        HarnessApp app = stack.App("app1");
        TracedClient raw = ContinuityDriver.RawOf(stack);
        List<Step> steps = [];

        // Rows 3, 4 and 9 read the session; rows 6 and 8 call the orders API.
        await using (Profile signedIn = await stack.OpenAsync())
        {
            await signedIn.SignInAsync(app);
            SessionCookies cookies = await ContinuityDriver.CookiesAsync(signedIn, app);
            steps.Add(await raw.SendAsync("session", HttpMethod.Get, app.Url(ContinuityDriver.SessionPath), cookies, 200, Ct));
            steps.Add(await raw.SendAsync("api", HttpMethod.Get, app.Url(ContinuityDriver.ApiPath), cookies, 200, Ct));

            // Row 9: the session ended in the browser, then the raw replay of its cookies.
            await ContinuityDriver.LogoutAsync(stack, signedIn, app);
            steps.Add(await raw.SendAsync("replay", HttpMethod.Get, app.Url(ContinuityDriver.SessionPath), cookies, Contract.Error("unauthenticated").Status, Ct));
        }

        // Row 8: the API call on a session whose access token is due.
        await using (Profile due = await stack.OpenAsync())
        {
            await stack.SignInWithExpiredAccessTokenAsync(due, app);
            SessionCookies cookies = await ContinuityDriver.CookiesAsync(due, app);
            steps.Add(await raw.SendAsync("api-due", HttpMethod.Get, app.Url(ContinuityDriver.ApiPath), cookies, 200, Ct));
        }

        foreach (Step step in steps)
        {
            JsonElement line = await RequestLineOfAsync(stack, step);
            string? rule = line.TryGetProperty("rule", out JsonElement value) ? value.GetString() : null;
            Assert.False(rule?.StartsWith("gate.", StringComparison.Ordinal) == true, $"{step.Name} was refused by the request gate's {rule}.");
            Assert.Equal((step.Name, step.Expect), (step.Name, step.Status));
        }

        // The pass is the fetch metadata's, not a lenient gate's: every raw exchange carried it.
        Exchange[] carried = [.. stack.LoadBalancer.Exchanges.Where(exchange => steps.Any(step => exchange.Sent(ClientTrace.Header)?.Contains(step.TraceId, StringComparison.Ordinal) == true))];
        Assert.Equal(steps.Count, carried.Length);
        Assert.All(carried, sent => Assert.Equal(("same-origin", "cors", "empty"), (sent.Sent("Sec-Fetch-Site"), sent.Sent("Sec-Fetch-Mode"), sent.Sent("Sec-Fetch-Dest"))));
    }

    [Fact]
    public async Task A_raw_call_without_fetch_metadata_is_refused_by_the_gate_on_this_stack()
    {
        // The gate is live here: the bare session read #348's sitting sent is refused gate.fetch-site,
        // so the test above passes on the raw client's metadata and on nothing the harness relaxes.
        await using LocalStack stack = await LocalStack.StartAsync();
        HarnessApp app = stack.App("app1");
        await using Profile profile = await stack.OpenAsync();
        await profile.SignInAsync(app);
        SessionCookies cookies = await ContinuityDriver.CookiesAsync(profile, app);
        LogMark mark = stack.MarkLog();

        using HttpRequestMessage bare = new(HttpMethod.Get, app.Url(ContinuityDriver.SessionPath));
        bare.Headers.TryAddWithoutValidation("Cookie", cookies.Reveal());
        using HttpResponseMessage answer = await stack.SendRawAsync(bare);

        Assert.Equal(Contract.Error("origin_rejected").Status, (int)answer.StatusCode);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.GateFetchSite, "app1");
    }

    /// <summary>The step's request line: the gateway takes the client's sampled trace id (Q137), so exactly one line carries it.</summary>
    private static async Task<JsonElement> RequestLineOfAsync(LocalStack stack, Step step)
    {
        JsonElement[] Lines() => [.. stack.RequestLines.Where(line => line.TryGetProperty("trace_id", out JsonElement id) && id.GetString() == step.TraceId)];

        // The line is written after the response (contracts/telemetry.md); in process it follows within milliseconds.
        for (int attempt = 0; attempt < 250 && Lines().Length == 0; attempt++)
        {
            await Task.Delay(20, Ct);
        }

        JsonElement[] lines = Lines();
        Assert.True(lines.Length == 1, $"{step.Name}: {lines.Length} request lines carry its trace id, not one.");
        return lines[0];
    }
}
