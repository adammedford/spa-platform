using System.Text.Json;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// A refusal's rule id as the deployed negative rows assert it (#211): read from the gateway's
/// CloudWatch log lines (<see cref="CellAws.WaitForGatewayLineAsync"/>), never from anything a browser
/// sees (contracts/request-gate.md -> "Rule ids"). A refusal is a request line, <c>msg</c> of
/// <c>request</c>, carrying the rule in <c>rule</c> and the app in <c>app_id</c> (contracts/telemetry.md).
/// </summary>
public sealed class CellRuleLog(CellAws aws, TimeSpan arrival)
{
    /// <summary>
    /// The request lines an assertion has already matched, by trace and span, in this process: a second
    /// refusal with the same rule must find its own line, not the first one's (N-06 asserts four in a
    /// row), and every stack in a sitting reads the cell's one log group.
    /// </summary>
    private static readonly HashSet<string> Claimed = [];

    /// <summary>
    /// Waits up to the arrival time for a request line from <paramref name="after"/> on that carries
    /// <paramref name="rule"/> (for <paramref name="appId"/>, when given) and that no earlier assertion
    /// matched, and claims it. Fails naming the rule and app if none comes.
    /// </summary>
    public async Task AssertLoggedAsync(DateTimeOffset after, string rule, string? appId, CancellationToken ct)
    {
        JsonElement line = await aws.WaitForGatewayLineAsync(
            after,
            line => line.TryGetProperty("msg", out JsonElement msg) && msg.GetString() == "request"
                && line.TryGetProperty("rule", out JsonElement value) && value.GetString() == rule
                && (appId is null || (line.TryGetProperty("app_id", out JsonElement app) && app.GetString() == appId))
                && !IsClaimed(line),
            arrival,
            $"a request line with rule {rule}{(appId is null ? "" : " for " + appId)} that no earlier assertion matched",
            ct);
        lock (Claimed)
        {
            Claimed.Add(ClaimOf(line));
        }
    }

    private static string ClaimOf(JsonElement line) =>
        $"{(line.TryGetProperty("trace_id", out JsonElement trace) ? trace.GetString() : "")}/{(line.TryGetProperty("span_id", out JsonElement span) ? span.GetString() : "")}";

    private static bool IsClaimed(JsonElement line)
    {
        lock (Claimed)
        {
            return Claimed.Contains(ClaimOf(line));
        }
    }
}
