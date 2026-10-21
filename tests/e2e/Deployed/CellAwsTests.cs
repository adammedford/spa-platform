using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed stack's two reads from AWS (#209), through the AWS CLI, against a recorded runner: a
/// test user's password from Parameter Store, and the gateway's log lines from CloudWatch Logs.
/// </summary>
public sealed class CellAwsTests
{
    private static Cell Cell => Cell.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")));

    private const string Canary = "Pa55-word!#canary";

    [Fact]
    public async Task A_test_users_password_is_read_decrypted_from_its_parameter_in_the_cells_region()
    {
        RecordedRunner runner = new(_ => Canary + "\n");
        CellAws aws = new(Cell, runner);

        Password password = await aws.PasswordAsync("alice", TestContext.Current.CancellationToken);

        Assert.Equal(Canary, password.Reveal());
        (string program, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal("aws", program);
        Assert.Equal(
            ["ssm", "get-parameter", "--name", "/spa-platform/poc/test-users/alice", "--with-decryption",
             "--query", "Parameter.Value", "--output", "text", "--region", "us-east-1"],
            arguments);
    }

    [Fact]
    public async Task A_password_shows_in_no_string_the_suite_could_print()
    {
        CellAws aws = new(Cell, new RecordedRunner(_ => Canary));
        Password password = await aws.PasswordAsync("alice", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Canary, password.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(password), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, $"{password}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_parameter_is_refused_without_reaching_the_form()
    {
        CellAws aws = new(Cell, new RecordedRunner(_ => "\n"));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aws.PasswordAsync("alice", TestContext.Current.CancellationToken));
        Assert.Contains("alice", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gateways_lines_since_a_moment_come_from_its_own_streams_and_only_json_lines_count()
    {
        DateTimeOffset since = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        RecordedRunner runner = new(_ => Events(
            ("gateway/gateway/0a1b", """{"msg":"request","rule":"gate.fetch_site","app_id":"app1"}"""),
            ("gateway/gateway/0a1b", "Now listening on: http://[::]:8080"),
            ("mock-api/mock-api/0a1b", """{"msg":"request","rule":"gate.fetch_site"}"""),
            ("gateway/gateway/0a1b", """{"msg":"Refreshed the access token for app app1","app_id":"app1"}""")));
        CellAws aws = new(Cell, runner);

        IReadOnlyList<JsonElement> lines = await aws.GatewayLinesAsync(since, TestContext.Current.CancellationToken);

        Assert.Equal(["request", "Refreshed the access token for app app1"], lines.Select(line => line.GetProperty("msg").GetString()));
        (string program, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal("aws", program);
        Assert.Equal(
            ["logs", "filter-log-events", "--log-group-name", "/spa-platform/poc/gateway", "--log-stream-name-prefix", "gateway/",
             "--start-time", "1760000000000", "--output", "json", "--region", "us-east-1"],
            arguments);
    }

    [Fact]
    public async Task A_wait_for_a_line_polls_until_one_matches()
    {
        int calls = 0;
        RecordedRunner runner = new(_ => ++calls < 3
            ? Events()
            : Events(("gateway/gateway/0a1b", """{"msg":"request","rule":"auth.callback","app_id":"app2"}""")));
        CellAws aws = new(Cell, runner) { PollEvery = TimeSpan.FromMilliseconds(1) };

        JsonElement line = await aws.WaitForGatewayLineAsync(
            DateTimeOffset.UnixEpoch, l => l.TryGetProperty("rule", out JsonElement rule) && rule.GetString() == "auth.callback",
            TimeSpan.FromSeconds(5), "a callback rejection", TestContext.Current.CancellationToken);

        Assert.Equal("app2", line.GetProperty("app_id").GetString());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_wait_that_times_out_fails_naming_what_it_waited_for()
    {
        CellAws aws = new(Cell, new RecordedRunner(_ => Events())) { PollEvery = TimeSpan.FromMilliseconds(1) };

        Exception failed = await Assert.ThrowsAnyAsync<Exception>(() => aws.WaitForGatewayLineAsync(
            DateTimeOffset.UnixEpoch, _ => true, TimeSpan.FromMilliseconds(30), "a refresh event", TestContext.Current.CancellationToken));
        Assert.Contains("a refresh event", failed.Message, StringComparison.Ordinal);
        Assert.Contains("/spa-platform/poc/gateway", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_call_reports_its_error_with_every_account_id_masked()
    {
        CommandFailedException failed = new("aws", ["logs", "filter-log-events"], 254,
            "An error occurred (AccessDeniedException): User arn:aws:sts::111122223333:assumed-role/x is not authorized");

        Assert.DoesNotContain("111122223333", failed.Message, StringComparison.Ordinal);
        Assert.Contains("AccessDeniedException", failed.Message, StringComparison.Ordinal);
        Assert.Contains("aws logs filter-log-events", failed.Message, StringComparison.Ordinal);
        Assert.Contains("254", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_release_an_app_serves_is_read_from_the_live_registry_in_the_cells_bucket()
    {
        RecordedRunner runner = new(_ => """{"revision":3,"apps":[{"appId":"app2","release":"r-2"},{"appId":"app1","release":"r-1"}]}""");
        CellAws aws = new(Cell, runner);

        Assert.Equal("r-1", await aws.ReleaseOfAsync("app1", TestContext.Current.CancellationToken));
        (string program, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal("aws", program);
        Assert.Equal(
            ["s3", "cp", "s3://spa-platform-poc-111122223333-us-east-1-an/registry/registry.json", "-", "--region", "us-east-1"],
            arguments);
        Assert.Contains("app3", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => aws.ReleaseOfAsync("app3", TestContext.Current.CancellationToken))).Message, StringComparison.Ordinal);
    }

    /// <summary>The AWS CLI's <c>filter-log-events</c> answer, merged across pages as the CLI does by default.</summary>
    private static string Events(params (string Stream, string Message)[] events) => JsonSerializer.Serialize(new
    {
        events = events.Select((e, i) => new { logStreamName = e.Stream, timestamp = 1_760_000_000_000L + i, message = e.Message, eventId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
    });

    private sealed class RecordedRunner(Func<string[], string> answer) : ICommandRunner
    {
        public List<(string Program, string[] Arguments)> Calls { get; } = [];

        public Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            string[] copy = [.. arguments];
            Calls.Add((program, copy));
            return Task.FromResult(answer(copy));
        }
    }
}
