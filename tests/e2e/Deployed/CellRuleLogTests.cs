using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed negative rows' rule assertion (#211), read from the gateway's CloudWatch log lines
/// through a recorded runner, as <see cref="CellAwsTests"/> proves #209's readers: a refusal counts only
/// as a request line with its rule (and app, when named) that no earlier assertion matched.
/// </summary>
public sealed class CellRuleLogTests
{
    private static Cell Cell => Cell.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")));

    private static readonly DateTimeOffset Mark = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    [Fact]
    public async Task A_rule_is_read_from_the_gateways_lines_since_the_mark()
    {
        string trace = Trace();
        RecordedRunner runner = new(_ => Events(Request(trace, "01", "gate.fetch-site", "app1")));
        CellRuleLog log = Log(runner);

        await log.AssertLoggedAsync(Mark, "gate.fetch-site", "app1", TestContext.Current.CancellationToken);

        (string program, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal("aws", program);
        Assert.Equal(["logs", "filter-log-events"], arguments[..2]);
        Assert.Equal("1760000000000", arguments[Array.IndexOf(arguments, "--start-time") + 1]);
    }

    [Fact]
    public async Task Only_a_request_line_with_the_rule_for_the_named_app_counts()
    {
        string trace = Trace();
        CellRuleLog log = Log(new RecordedRunner(_ => Events(
            """{"msg":"Refused under gate.origin","rule":"gate.origin","app_id":"app1","trace_id":"t","span_id":"s"}""",
            Request(trace, "01", "gate.origin", "app2"),
            Request(trace, "02", "gate.fetch-site", "app1"))));

        Exception missed = await Assert.ThrowsAnyAsync<Exception>(
            () => log.AssertLoggedAsync(Mark, "gate.origin", "app1", TestContext.Current.CancellationToken));

        Assert.Contains("gate.origin for app1", missed.Message, StringComparison.Ordinal);
        await log.AssertLoggedAsync(Mark, "gate.origin", "app2", TestContext.Current.CancellationToken);
        await log.AssertLoggedAsync(Mark, "gate.fetch-site", null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_second_assertion_of_the_same_rule_needs_a_line_of_its_own()
    {
        // N-06 refuses four times with one rule, N-07 three: each assertion must see its own refusal.
        string trace = Trace();
        CellRuleLog log = Log(new RecordedRunner(_ => Events(
            Request(trace, "01", "host.unknown", null),
            Request(trace, "02", "host.unknown", null))));

        await log.AssertLoggedAsync(Mark, "host.unknown", null, TestContext.Current.CancellationToken);
        await log.AssertLoggedAsync(Mark, "host.unknown", null, TestContext.Current.CancellationToken);
        Exception third = await Assert.ThrowsAnyAsync<Exception>(
            () => log.AssertLoggedAsync(Mark, "host.unknown", null, TestContext.Current.CancellationToken));

        Assert.Contains("no earlier assertion matched", third.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claim_holds_across_readers_in_the_process_as_the_cells_one_log_does()
    {
        // Every stack in a sitting reads the one log group, so a later stack must not reuse an earlier one's line.
        string trace = Trace();
        RecordedRunner runner = new(_ => Events(Request(trace, "01", "path.reserved", "app1")));

        await Log(runner).AssertLoggedAsync(Mark, "path.reserved", "app1", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(
            () => Log(runner).AssertLoggedAsync(Mark, "path.reserved", "app1", TestContext.Current.CancellationToken));
    }

    private static CellRuleLog Log(RecordedRunner runner) =>
        new(new CellAws(Cell, runner) { PollEvery = TimeSpan.FromMilliseconds(1) }, TimeSpan.FromMilliseconds(50));

    /// <summary>A trace id of this test's own: the claims are process-wide, as on the cell.</summary>
    private static string Trace() => Guid.NewGuid().ToString("N");

    private static string Request(string trace, string span, string rule, string? appId) => JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["msg"] = "request",
        ["trace_id"] = trace,
        ["span_id"] = span,
        ["rule"] = rule,
        ["app_id"] = appId,
    });

    /// <summary>The AWS CLI's <c>filter-log-events</c> answer, every line from the gateway's stream.</summary>
    private static string Events(params string[] messages) => JsonSerializer.Serialize(new
    {
        events = messages.Select((message, i) => new { logStreamName = "gateway/gateway/0a1b", timestamp = 1_760_000_000_000L + i, message, eventId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
    });
}
