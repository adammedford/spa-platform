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

    [Fact]
    public async Task The_running_task_is_the_services_one_running_task_by_id_revision_and_start_with_no_account_in_it()
    {
        RecordedRunner runner = new(arguments => arguments[1] == "list-tasks"
            ? """{"taskArns":["arn:aws:ecs:us-east-1:111122223333:task/spa-poc/0a1b2c3d4e5f"]}"""
            : """{"tasks":[{"taskArn":"arn:aws:ecs:us-east-1:111122223333:task/spa-poc/0a1b2c3d4e5f","taskDefinitionArn":"arn:aws:ecs:us-east-1:111122223333:task-definition/spa-platform-poc:4","startedAt":"2026-10-21T02:00:00.123000+00:00","lastStatus":"RUNNING"}],"failures":[]}""");
        CellAws aws = new(Cell, runner);

        RunningTask task = await aws.RunningTaskAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new RunningTask("0a1b2c3d4e5f", "spa-platform-poc:4", new DateTimeOffset(2026, 10, 21, 2, 0, 0, 123, TimeSpan.Zero)), task);
        Assert.DoesNotContain("111122223333", task.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [["ecs", "list-tasks", "--cluster", "spa-poc", "--service-name", "gateway", "--desired-status", "RUNNING", "--output", "json", "--region", "us-east-1"],
             ["ecs", "describe-tasks", "--cluster", "spa-poc", "--tasks", "arn:aws:ecs:us-east-1:111122223333:task/spa-poc/0a1b2c3d4e5f", "--output", "json", "--region", "us-east-1"]],
            runner.Calls.Select(call => call.Arguments));
    }

    [Theory]
    [InlineData("""{"taskArns":[]}""", "0")]
    [InlineData("""{"taskArns":["arn:aws:ecs:us-east-1:111122223333:task/spa-poc/a","arn:aws:ecs:us-east-1:111122223333:task/spa-poc/b"]}""", "2")]
    public async Task A_service_without_exactly_one_running_task_is_refused_since_its_identity_is_then_no_one_tasks(string listed, string count)
    {
        // During a deployment the old and the new task both run (#207), and before and after would then compare two different things.
        CellAws aws = new(Cell, new RecordedRunner(_ => listed));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => aws.RunningTaskAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"{count} running tasks", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("111122223333", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_live_registrys_revision_and_apps_are_read_from_the_cells_bucket()
    {
        CellAws aws = new(Cell, new RecordedRunner(_ => """{"revision":7,"apps":[{"appId":"app2","release":"r-2"},{"appId":"app1","release":"r-1"}]}"""));

        LiveRegistry registry = await aws.LiveRegistryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, registry.Revision);
        Assert.Equal(["app2", "app1"], registry.AppIds);
    }

    [Fact]
    public async Task A_cancelled_run_gives_its_program_the_grace_to_stop_by_itself()
    {
        // Ctrl-C interrupts the program too, and an interrupted terraform apply gives its state lock back
        // if it is let finish; a kill the moment the run is cancelled would leave the lock behind.
        using TempDir dir = new();
        string pid = dir.File("pid"), marker = dir.File("stopped");
        using CancellationTokenSource cancel = new();
        ProcessCommandRunner runner = new() { StopGrace = TimeSpan.FromSeconds(10) };
        Task<string> run = runner.RunAsync("/bin/sh", ["-c", $"echo $$ > '{pid}'; sleep 1; echo stopped > '{marker}'"], cancel.Token);

        await PidOfAsync(pid);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(File.Exists(marker), "the program was killed before it could stop by itself");
    }

    [Fact]
    public async Task A_cancelled_run_whose_program_does_not_stop_kills_it_and_what_it_started_after_the_grace()
    {
        // A run cancelled other than from a terminal sends the program nothing, and a terraform apply
        // left running would keep the apps root's state lock after its test had gone.
        using TempDir dir = new();
        string pid = dir.File("pid"), child = dir.File("child");
        using CancellationTokenSource cancel = new();
        ProcessCommandRunner runner = new() { StopGrace = TimeSpan.FromMilliseconds(200) };
        Task<string> run = runner.RunAsync("/bin/sh", ["-c", $"sleep 60 & echo $! > '{child}'; echo $$ > '{pid}'; wait"], cancel.Token);

        int program = await PidOfAsync(pid);
        int started = await PidOfAsync(child);
        try
        {
            // The cancellation reaches the caller long before the program would have ended by itself.
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.True(await GoneWithinAsync(program), $"the program, pid {program}, is still running");
            Assert.True(await GoneWithinAsync(started), $"what the program started, pid {started}, is still running");
        }
        finally
        {
            foreach (int left in new[] { program, started }.Where(Running))
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(left);
                process.Kill();
            }
        }
    }

    [Fact]
    public async Task An_exit_the_command_accepts_comes_back_with_its_output_and_any_other_fails()
    {
        // terraform plan -detailed-exitcode exits 2 when the plan has changes, the answer F-07's onboarding plan wants.
        ProcessCommandRunner runner = new();
        Command changes = new("/bin/sh", ["-c", "echo planned; exit 2"]) { AcceptedExits = [0, 2] };

        CommandResult result = await runner.RunAsync(changes, TestContext.Current.CancellationToken);
        Assert.Equal(new CommandResult(2, "planned\n"), result);

        CommandFailedException failed = await Assert.ThrowsAsync<CommandFailedException>(
            () => runner.RunAsync(changes with { Arguments = ["-c", "echo broken >&2; exit 1"] }, TestContext.Current.CancellationToken));
        Assert.Contains("exited 1: broken", failed.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CommandFailedException>(() => runner.RunAsync("/bin/sh", ["-c", "exit 2"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_commands_environment_is_set_over_the_callers()
    {
        // The onboarding runs spactl and Terraform as the project's profile whatever the shell exported (#212).
        Command printed = new("/bin/sh", ["-c", "printf %s \"$AWS_PROFILE\""]) { Environment = new Dictionary<string, string> { ["AWS_PROFILE"] = "the-project" } };

        CommandResult result = await new ProcessCommandRunner().RunAsync(printed, TestContext.Current.CancellationToken);

        Assert.Equal("the-project", result.Output);
    }

    /// <summary>A directory of the test's own, removed with everything in it.</summary>
    private sealed class TempDir : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("process-runner-");

        public string File(string name) => Path.Combine(directory.FullName, name);

        public void Dispose() => directory.Delete(recursive: true);
    }

    private static async Task<int> PidOfAsync(string file)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(file) && int.TryParse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), out int pid))
            {
                return pid;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"the program never wrote {file}");
    }

    private static async Task<bool> GoneWithinAsync(int pid)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (Running(pid) && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return !Running(pid);
    }

    /// <summary>
    /// Whether a process runs. Where there is a /proc, a zombie, state Z, is not running: a killed
    /// grandchild is reparented, and the parent it gets (a container's first process, say) may never
    /// reap it (https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html).
    /// </summary>
    private static bool Running(int pid)
    {
        if (Directory.Exists("/proc"))
        {
            string stat = $"/proc/{pid}/stat";
            if (!File.Exists(stat))
            {
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(stat);
            }
            catch (IOException)
            {
                return false;
            }

            // The state follows the command's closing parenthesis, which the command itself may contain.
            return text[(text.LastIndexOf(')') + 2)..].FirstOrDefault() != 'Z';
        }

        try
        {
            using System.Diagnostics.Process found = System.Diagnostics.Process.GetProcessById(pid);
            return !found.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The AWS CLI's <c>filter-log-events</c> answer, merged across pages as the CLI does by default.</summary>
    private static string Events(params (string Stream, string Message)[] events) => JsonSerializer.Serialize(new
    {
        events = events.Select((e, i) => new { logStreamName = e.Stream, timestamp = 1_760_000_000_000L + i, message = e.Message, eventId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
    });
}
