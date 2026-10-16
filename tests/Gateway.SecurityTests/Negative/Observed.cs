using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The log, read as a client's operator would read the deployed stream: whole lines that the
/// gateway's own JSON formatter wrote. It is the fourth assertion surface of this suite, beside the
/// test browser, the raw client and the upstreams, and the only one that can show <i>which rule</i>
/// refused a request: <c>contracts/http-contract.md</c> says the <c>code</c> in a problem body
/// "says little on purpose", and <c>contracts/request-gate.md</c> puts the rule id in the log line
/// and in <c>security.rejections</c>.
///
/// <para>Every rule id this suite asserts comes from <see cref="Platform.Abstractions.RuleIds"/>,
/// never a string literal, so renaming one is a compile error rather than a silent test gap
/// (ADR-0015).</para>
/// </summary>
internal static class Observed
{
    /// <summary>
    /// Waits for <paramref name="ready"/> and fails if it never becomes true. UsePlatformTelemetry writes the
    /// request line after the rest of the pipeline has returned
    /// (<c>contracts/telemetry.md</c> -> "How <c>rule</c> and <c>session_ref</c> reach the request
    /// line"), so a test that has already read the response may still be ahead of its own log line.
    /// It fails rather than returning, because a missing line must never be a pass.
    /// </summary>
    public static async Task UntilAsync(Func<bool> ready, string what, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 250 && !ready(); attempt++)
        {
            await Task.Delay(20, ct);
        }

        Assert.True(ready(), $"waited five seconds for {what}, which never arrived.");
    }

    /// <summary>The request lines the telemetry stage wrote: one per request, <c>msg</c> of <c>request</c>.</summary>
    public static IReadOnlyList<JsonElement> RequestLines(GatewayHarness harness) =>
        [.. harness.Log.Json.Where(line => Field(line, "msg") == "request")];

    /// <summary>
    /// The rule id on the <b>one</b> request line the gateway wrote after <paramref name="before"/>
    /// lines, which is how a test names the rule of the request it just made. Taking the last line
    /// would read whichever request finished last, so the count is taken before the request and the
    /// one new line is required to be the only one.
    /// </summary>
    public static async Task<string?> RuleOfOneRequestAsync(GatewayHarness harness, int before, CancellationToken ct)
    {
        await UntilAsync(() => RequestLines(harness).Count > before, "the request's own log line", ct);

        IReadOnlyList<JsonElement> lines = RequestLines(harness);
        Assert.Equal(before + 1, lines.Count);
        return Field(lines[before], "rule");
    }

    /// <summary>
    /// How many request lines the gateway has written, once it has stopped writing them: the count a
    /// test takes <b>before</b> the request it is about to make.
    ///
    /// <para>The wait is not caution. The telemetry stage writes its line after the rest of the pipeline has
    /// returned, which is after the response has reached the client, so a test that read the count
    /// the instant a previous response arrived could take it before that response's own line landed
    /// and then attribute two lines to one request. Settling first is what makes "the one line this
    /// request produced" a sound reading, and it is why this is a <c>Task&lt;int&gt;</c> and not a
    /// property.</para>
    /// </summary>
    public static async Task<int> SettledCountAsync(GatewayHarness harness, CancellationToken ct)
    {
        int count = RequestLines(harness).Count;
        for (int stable = 0; stable < 3;)
        {
            await Task.Delay(20, ct);
            int now = RequestLines(harness).Count;
            stable = now == count ? stable + 1 : 0;
            count = now;
        }

        return count;
    }

    /// <summary>One string field of a line, or null when the field is absent or null.</summary>
    public static string? Field(JsonElement line, string name) =>
        line.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One numeric field of a line, or null.</summary>
    public static long? Number(JsonElement line, string name) =>
        line.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
}
