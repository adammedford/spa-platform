using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// F-07's procedure on the cell (#212; docs/plan/05-infrastructure-and-cost.md -> Onboarding a third
/// SPA), against a recorded runner standing in for Terraform, the AWS CLI and spactl, and a stand-in for
/// the tunnel: the apps root's plan with the onboarding definition, run with <c>-detailed-exitcode</c>,
/// is read before it is applied and applied only when it adds the app's own resources; spactl, run as
/// the project's profile, publishes the release, adds the app and waits for the revision on every
/// running task; the app then answers through the tunnel with a login at its own client; the gateway's
/// running tasks are the same set before and after; and the clean-up takes the app out of the registry,
/// and waits on every running task, before its client and secret go. A service that is not steady is
/// refused before any plan (#873): one recording per guard, each with that guard's defect alone, so
/// removing the guard lets the onboarding reach Terraform.
/// </summary>
public sealed class CellOnboardingTests : IDisposable
{
    private const string NewClientId = "3example33333333";
    private const string Canary = "client-secret-canary-0b1c";

    /// <summary>A profile no workstation names, so a command that carries it was given it by the onboarding.</summary>
    private static readonly Dictionary<string, string> Project = new() { ["AWS_PROFILE"] = "onboarding-tests-project", ["AWS_REGION"] = "us-east-1" };

    private static readonly RunningTask Gateway = new("0a1b2c3d4e5f", "spa-platform-poc:4", new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero));

    /// <summary>The second task of a two-task service, which is a cell's steady state when its <c>multi_task</c> is on (infra/modules/cell/ecs.tf).</summary>
    private static readonly RunningTask Other = new("9f8e7d6c5b4a", "spa-platform-poc:4", new DateTimeOffset(2026, 10, 21, 2, 1, 0, TimeSpan.Zero));

    private static readonly string Example = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json"));

    private static readonly string[] App3Resources =
        ["aws_cognito_managed_login_branding.app[\"app3\"]", "aws_cognito_user_pool_client.app[\"app3\"]", "aws_ssm_parameter.client_secret[\"app3\"]"];

    private readonly string scratch = Directory.CreateTempSubdirectory("spa-e2e-onboarding-tests-").FullName;

    /// <summary>A stand-in apps root: the real <c>main.tf</c>, initialised.</summary>
    private readonly string appsRoot;

    private readonly string outputsPath;

    public CellOnboardingTests()
    {
        appsRoot = Path.Combine(scratch, "apps");
        Directory.CreateDirectory(Path.Combine(appsRoot, ".terraform"));
        File.Copy(Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps", "main.tf"), Path.Combine(appsRoot, "main.tf"));
        outputsPath = Path.Combine(scratch, "outputs.json");
        File.WriteAllText(outputsPath, Example);
    }

    [Fact]
    public async Task Onboarding_reads_the_plan_applies_exactly_it_then_publishes_adds_waits_and_finds_the_app_answering_through_the_tunnel()
    {
        CellState cellState = new();
        string? outputsSpactlRead = null;
        RecordedRunner runner = RecordedRunner.WithExits(command =>
        {
            if (command.Program == "spactl" && command.Arguments[0] == "app" && command.Arguments[1] == "add")
            {
                outputsSpactlRead = File.ReadAllText(Option([.. command.Arguments], "--outputs"));
                cellState.Add("app3");
            }

            return cellState.Answer(command, App3Resources.Select(address => (address, "create")).ToArray());
        });
        Tunnel tunnel = new(runner);

        Onboarded onboarded = await Onboarding(runner, tunnel).OnboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["aws s3", "aws ecs", "aws ecs", "aws ecs", "terraform plan", "terraform show", "terraform apply", "terraform output", "spactl release", "spactl app", "aws s3", "spactl wait", "aws ecs", "aws ecs"],
            runner.Calls.Select(call => $"{call.Program} {Verb(call)}"));

        string[] plan = Call(runner, "terraform", "plan");
        Assert.Contains("""definition_dirs=["../../../../fixtures/apps","../../../../fixtures/onboarding"]""", plan);
        Assert.Contains("-detailed-exitcode", plan);
        string planFile = Option(plan, "-out=", inline: true);
        Assert.Equal(planFile, Call(runner, "terraform", "show")[^1]);
        Assert.Equal(planFile, Call(runner, "terraform", "apply")[^1]);
        Assert.Equal("-chdir=" + appsRoot, Call(runner, "terraform", "apply")[0]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(planFile)), "the plan holds every client secret in cleartext and must not outlive the run.");

        Assert.Equal(App3Resources.Select(address => new PlannedChange(address, "create")), onboarded.Planned);
        Assert.Equal(NewClientId, onboarded.App.ClientId);
        Assert.Equal(FixtureFiles.AppDefinition("app3")["host"]!.GetValue<string>(), onboarded.App.Host);

        // spactl reads app 3's new client from a copy of the outputs, which keeps every other name.
        JsonNode read = JsonNode.Parse(outputsSpactlRead!)!;
        Assert.Equal(NewClientId, read["clients"]!["app3"]!["clientId"]!.GetValue<string>());
        Assert.Equal("1example23456789", read["clients"]!["app1"]!["clientId"]!.GetValue<string>());
        Assert.Equal("spa-platform-poc-111122223333-us-east-1-an", read["bucket"]!.GetValue<string>());
        Assert.Equal(Example, File.ReadAllText(outputsPath)); // The repository's document is workstream 1's, written by its scripts.

        string[] publish = Call(runner, "spactl", "release");
        Assert.Equal(["release", "publish", "app3", Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "spa", "app3")], publish[..4]);
        string[] add = Call(runner, "spactl", "app");
        Assert.Equal(["app", "add", Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "onboarding", "app3.json"), "--release", publish[4]], add[..5]);
        Assert.Equal("/config/platform.json", Option(add, "--config"));

        string[] wait = Call(runner, "spactl", "wait");
        Assert.Equal(["wait", "--url", "https://app1.spa-poc.test:9443/", "--revision", "8"], wait[..5]);
        Assert.Equal(Gateway.TaskId, Option(wait, "--instances"));
        Assert.Equal(8, onboarded.Revision);

        // After the wait, app 3's host answered a login navigation through the tunnel, sending it to the provider with app 3's own client.
        Tunnel.Sent login = Assert.Single(tunnel.Requests);
        Assert.Equal(runner.Calls.FindIndex(call => call.Program == "spactl" && Verb(call) == "wait") + 1, login.CallsBefore);
        Assert.Equal(onboarded.App.Url("/bff/login"), login.Url);
        Assert.Equal("navigate", login.FetchMode);

        // The same task throughout, read before the plan and after the app answered.
        Assert.Equal(Gateway, Assert.Single(onboarded.Tasks));
        Assert.Equal(2, cellState.TasksDescribed);
    }

    [Fact]
    public async Task Spactl_and_terraform_run_as_the_projects_profile_and_every_other_command_as_the_caller()
    {
        // The project's profile pins the account the onboarding writes to (tools/aws-project.env); a shell left on another profile would onboard app 3 elsewhere.
        CellState cellState = new();
        RecordedRunner runner = RecordedRunner.WithExits(command =>
        {
            if (command.Program == "spactl" && command.Arguments[0] == "app")
            {
                cellState.Add("app3");
            }

            return cellState.Answer(command, App3Resources.Select(address => (address, "create")).ToArray());
        });

        await Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.All(runner.Commands.Where(command => command.Program is "spactl" or "terraform"), command => Assert.Equal(Project, command.Environment));
        Assert.Contains(runner.Commands, command => command.Program == "spactl");
        Assert.All(runner.Commands.Where(command => command.Program == "aws"), command => Assert.Empty(command.Environment));
    }

    [Fact]
    public async Task A_plan_whose_detailed_exit_says_no_changes_is_refused_before_anything_is_applied()
    {
        // -detailed-exitcode exits 0 for an empty plan, which cannot show an onboarding.
        CellState cellState = new();
        RecordedRunner runner = RecordedRunner.WithExits(command => cellState.Answer(command));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("no resource change", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] is "show" or "apply");
        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl");
    }

    [Fact]
    public async Task A_plan_that_fails_is_a_failure_and_not_an_empty_plan()
    {
        // Exit 1 is an error under -detailed-exitcode; read as "no changes", the clean-up would return as if app 3 were gone.
        CellState cellState = new() { PlanExit = 1 };
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => command.Program == "spactl" && command.Arguments[1] == "remove"
            ? cellState.Removed("app3")
            : cellState.Answer(command));

        await Assert.ThrowsAsync<CommandFailedException>(() => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("9f8e7d6c5b4a", "spa-platform-poc:4")]
    [InlineData("0a1b2c3d4e5f", "spa-platform-poc:5")]
    public async Task A_gateway_task_that_changed_during_the_onboarding_fails_it_naming_both(string taskId, string revision)
    {
        // No restart, no new task definition, no deployment (04-tests F-07, tier D).
        RunningTask after = new(taskId, revision, Gateway.StartedAt);
        CellState cellState = new() { Tasks = [[Gateway], [after]] };
        RecordedRunner runner = RecordedRunner.WithExits(command =>
        {
            if (command.Program == "spactl" && command.Arguments[0] == "app")
            {
                cellState.Add("app3");
            }

            return cellState.Answer(command, App3Resources.Select(address => (address, "create")).ToArray());
        });

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains(Gateway.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Contains(after.ToString(), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, null)]
    [InlineData(302, "https://elsewhere.example/oauth2/authorize?client_id=3example33333333")]
    [InlineData(302, "https://spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com/oauth2/authorize?client_id=1example23456789")]
    public async Task An_app_that_does_not_answer_through_the_tunnel_with_a_login_at_its_own_client_fails_the_onboarding_showing_no_state(int status, string? location)
    {
        CellState cellState = new();
        RecordedRunner runner = RecordedRunner.WithExits(command =>
        {
            if (command.Program == "spactl" && command.Arguments[0] == "app")
            {
                cellState.Add("app3");
            }

            return cellState.Answer(command, App3Resources.Select(address => (address, "create")).ToArray());
        });
        Tunnel tunnel = new(runner) { Status = status, Location = location is null ? null : location + "&state=" + Canary };

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, tunnel).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("app3", refused.Message, StringComparison.Ordinal);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_changes_anything_but_the_new_apps_resources_is_not_applied_and_the_refusal_shows_no_value()
    {
        CellState cellState = new();
        RecordedRunner runner = RecordedRunner.WithExits(command => cellState.Answer(command,
            [.. App3Resources.Select(address => (address, "create")), ("aws_cognito_user_pool_client.app[\"app1\"]", "update")]));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("aws_cognito_user_pool_client.app[\"app1\"] update", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] == "apply");
        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl");
    }

    [Fact]
    public async Task An_app_already_in_the_live_registry_is_refused_before_anything_is_planned()
    {
        // Its plan could not show the onboarding, so "adds only the per-app resources" would be read from nothing.
        CellState cellState = new();
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => cellState.Answer(command));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("app3", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program != "aws");
    }

    [Fact]
    public async Task An_app_the_outputs_document_already_names_is_refused_naming_the_rewrite_the_clean_up_does_not_do()
    {
        // The clean-up never writes the outputs document, so "run again after the clean-up" would refuse forever.
        JsonObject outputs = JsonNode.Parse(Example)!.AsObject();
        outputs["clients"]!["app3"] = outputs["clients"]!["app1"]!.DeepClone();
        Cell cell = Cell.Parse(outputs.ToJsonString());
        RecordedRunner runner = RecordedRunner.WithExits(command => new CellState().Answer(command));
        CellOnboarding onboarding = new(cell, new CellAws(cell, runner), runner, Setup(new Tunnel(runner)));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => onboarding.OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("seed.sh", refused.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_clean_up_on_a_root_not_initialised_leaves_the_registry_alone()
    {
        // Removing the app first would leave its client and secret live with nothing to destroy them.
        Directory.Delete(Path.Combine(appsRoot, ".terraform"));
        CellState cellState = new();
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => cellState.Answer(command));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task An_apps_root_not_initialised_is_refused_naming_its_readme()
    {
        Directory.Delete(Path.Combine(appsRoot, ".terraform"));
        RecordedRunner runner = RecordedRunner.WithExits(command => new CellState().Answer(command));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("README.md", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform");
    }

    [Fact]
    public async Task The_clean_up_removes_the_app_from_the_registry_and_waits_before_it_destroys_its_resources()
    {
        CellState cellState = new();
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => command.Program == "spactl" && command.Arguments[1] == "remove"
            ? cellState.Removed("app3")
            : cellState.Answer(command, App3Resources.Select(address => (address, "delete")).ToArray()));

        await Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["aws ecs", "aws ecs", "aws s3", "spactl app", "aws s3", "spactl wait", "terraform plan", "terraform show", "terraform apply"],
            runner.Calls.Select(call => $"{call.Program} {Verb(call)}"));
        Assert.Equal(["app", "remove", "app3"], Call(runner, "spactl", "app")[..3]);
        Assert.Equal("9", Option(Call(runner, "spactl", "wait"), "--revision"));
        Assert.Equal(Gateway.TaskId, Option(Call(runner, "spactl", "wait"), "--instances"));
        Assert.Contains("-detailed-exitcode", Call(runner, "terraform", "plan"));
        Assert.DoesNotContain(Call(runner, "terraform", "plan"), argument => argument.Contains("definition_dirs", StringComparison.Ordinal));
        Assert.All(runner.Commands.Where(command => command.Program is "spactl" or "terraform"), command => Assert.Equal(Project, command.Environment));
    }

    [Fact]
    public async Task A_clean_up_with_nothing_left_applies_nothing()
    {
        RecordedRunner runner = RecordedRunner.WithExits(command => new CellState().Answer(command));

        await Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl" || (call.Program == "terraform" && call.Arguments[1] is "show" or "apply"));
    }

    [Fact]
    public async Task A_clean_up_plan_that_would_destroy_anything_else_is_not_applied()
    {
        RecordedRunner runner = RecordedRunner.WithExits(command => new CellState().Answer(command,
            [.. App3Resources.Select(address => (address, "delete")), ("aws_ssm_parameter.client_secret[\"app1\"]", "delete")]));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("aws_ssm_parameter.client_secret[\"app1\"] delete", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] == "apply");
    }

    [Fact]
    public async Task A_steady_two_task_service_onboards_waiting_for_the_revision_on_both_tasks()
    {
        // One load-balanced read shows the revision on whichever task answered (#358's Answer); the other may still serve the snapshot before it.
        CellState cellState = new() { Tasks = [[Gateway, Other]] };
        RecordedRunner runner = OnboardingRunner(cellState);

        Onboarded onboarded = await Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken);

        string[] wait = Call(runner, "spactl", "wait");
        Assert.Equal("8", Option(wait, "--revision"));
        Assert.Equal([Gateway.TaskId, Other.TaskId], Option(wait, "--instances").Split(',').Order(StringComparer.Ordinal));
        Assert.True(onboarded.Tasks.SetEquals([Gateway, Other]), $"the onboarding reports {string.Join("; ", onboarded.Tasks)}");
    }

    [Fact]
    public async Task A_steady_two_task_service_is_taken_off_waiting_for_the_removal_revision_on_both_tasks_before_anything_is_destroyed()
    {
        CellState cellState = new() { Tasks = [[Gateway, Other]] };
        cellState.Add("app3");
        RecordedRunner runner = OffboardingRunner(cellState);

        await Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken);

        string[] wait = Call(runner, "spactl", "wait");
        Assert.Equal("9", Option(wait, "--revision")); // 8 named the app; 9 is the revision that removed it.
        Assert.Equal([Gateway.TaskId, Other.TaskId], Option(wait, "--instances").Split(',').Order(StringComparer.Ordinal));

        int Index(Func<(string Program, string[] Arguments), bool> match) => runner.Calls.FindIndex(call => match(call));
        int listed = Index(call => call.Program == "aws" && call.Arguments[1] == "list-tasks");
        int removed = Index(call => call.Program == "spactl" && call.Arguments[0] == "app");
        int waited = Index(call => call.Program == "spactl" && call.Arguments[0] == "wait");
        int applied = Index(call => call.Program == "terraform" && call.Arguments[1] == "apply");
        Assert.True(listed >= 0 && listed < removed && removed < waited && waited < applied, $"the running set read at {listed}, the removal at {removed}, the wait at {waited}, the apply at {applied}");
    }

    [Fact]
    public async Task A_removal_revision_a_task_never_reports_leaves_the_client_and_secret_alone()
    {
        // The other task may still serve a snapshot that names the secret (#1071 Q51), so the wait's failure stops the clean-up.
        CellState cellState = new() { Tasks = [[Gateway, Other]] };
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => command.Program == "spactl" && command.Arguments[0] == "wait"
            ? new CommandResult(1, "")
            : OffboardingAnswer(cellState, command));

        await Assert.ThrowsAsync<CommandFailedException>(() => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform");
    }

    [Fact]
    public async Task A_second_clean_up_after_a_failed_wait_waits_on_every_task_before_anything_is_destroyed()
    {
        // The first took the app out of the registry and stopped on its wait, so the other task may still name the secret (#1071 Q51; #1141).
        CellState cellState = new() { Tasks = [[Gateway, Other]] };
        await FailedFirstCleanUpAsync(cellState);
        RecordedRunner runner = OffboardingRunner(cellState);

        await Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken);

        string[] wait = Call(runner, "spactl", "wait");
        Assert.Equal("9", Option(wait, "--revision")); // The first clean-up's removal, still the live revision.
        Assert.Equal([Gateway.TaskId, Other.TaskId], Option(wait, "--instances").Split(',').Order(StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl" && call.Arguments[0] == "app");

        int waited = runner.Calls.FindIndex(call => call.Program == "spactl" && call.Arguments[0] == "wait");
        int applied = runner.Calls.FindIndex(call => call.Program == "terraform" && call.Arguments[1] == "apply");
        Assert.True(waited >= 0 && waited < applied, $"the wait at {waited}, the apply at {applied}");
    }

    [Fact]
    public async Task A_second_clean_up_whose_wait_fails_too_leaves_the_client_and_secret_alone()
    {
        CellState cellState = new() { Tasks = [[Gateway, Other]] };
        await FailedFirstCleanUpAsync(cellState);
        RecordedRunner runner = RecordedRunner.WithExits(command => command.Program == "spactl" && command.Arguments[0] == "wait"
            ? new CommandResult(1, "")
            : OffboardingAnswer(cellState, command));

        await Assert.ThrowsAsync<CommandFailedException>(() => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains(runner.Calls, call => call.Program == "spactl" && call.Arguments[0] == "wait");
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] == "apply");
    }

    [Fact]
    public async Task A_clean_up_with_no_running_task_to_wait_on_leaves_the_registry_alone()
    {
        // As wait_for_revision refuses it (tools/scripts/cell-common.sh): no wait could finish, so the removal would leave the app half taken off.
        CellState cellState = new() { Tasks = [[]] };
        cellState.Add("app3");
        RecordedRunner runner = OffboardingRunner(cellState);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("no running task", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program is "spactl" or "terraform");
    }

    [Fact]
    public async Task A_clean_up_with_no_running_task_and_nothing_to_wait_for_still_destroys_what_is_left()
    {
        // The app is out of the registry, whether it never reached it or an earlier clean-up took it off, and no task is
        // serving, so no snapshot can name its secret and spactl wait, which refuses an empty --instances, has nothing to wait on.
        RecordedRunner runner = OffboardingRunner(new CellState { Tasks = [[]] });

        await Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Contains(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] == "apply");
        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl");
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("FAILED")]
    [InlineData(null)]
    public async Task A_primary_rollout_that_has_not_completed_is_refused_before_any_plan(string? rollout)
    {
        // services-stable returned while rolloutState was still IN_PROGRESS (#467), so the count alone does not show it.
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[Gateway, Other]],
            Service = Service(Deployment("PRIMARY", rollout, 2, 2)),
        });

        Assert.Contains("not COMPLETED", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_deployments_are_refused_before_any_plan()
    {
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[Gateway, Other]],
            Service = Service(Deployment("PRIMARY", "COMPLETED", 2, 2), Deployment("ACTIVE", "COMPLETED", 0, 0)),
        });

        Assert.Contains("2 deployments", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_ecs_does_not_find_is_refused_before_any_plan()
    {
        // The answer holds failures beside services (https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeServices.html); no service to read must not pass for a steady one.
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[Gateway, Other]],
            Service = """{"services":[],"failures":[{"reason":"MISSING"}]}""",
        });

        Assert.Contains("0 deployments", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_deployment_that_is_not_the_primary_is_refused_before_any_plan()
    {
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[Gateway, Other]],
            Service = Service(Deployment("ACTIVE", "COMPLETED", 2, 2)),
        });

        Assert.Contains("not COMPLETED", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_primary_running_below_its_desired_count_is_refused_before_any_plan()
    {
        // Two tasks are listed, so only the deployment's own count shows this one.
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[Gateway, Other]],
            Service = Service(Deployment("PRIMARY", "COMPLETED", 1, 2)),
        });

        Assert.Contains("1 of 2", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Listed_tasks_that_do_not_number_the_desired_count_are_refused_before_any_plan(int listed)
    {
        // The deployment's own counts are right, so only the list shows this one.
        RunningTask[] tasks = [Gateway, Other, new("c0ffee00c0ff", Gateway.Revision, Gateway.StartedAt)];
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [tasks[..listed]],
            Service = Service(Deployment("PRIMARY", "COMPLETED", 2, 2)),
        });

        Assert.Contains($"{listed} RUNNING", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_steady_at_no_tasks_is_refused_before_any_plan()
    {
        // Every count agrees at zero, and spactl wait refuses an empty --instances after the apply.
        InvalidOperationException refused = await RefusedBeforeAnyPlanAsync(new CellState
        {
            Tasks = [[]],
            Service = Service(Deployment("PRIMARY", "COMPLETED", 0, 0)),
        });

        Assert.Contains("no running task", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("restarted")]
    [InlineData("redefined")]
    [InlineData("gone")]
    [InlineData("added")]
    public async Task A_change_to_either_of_two_tasks_during_the_onboarding_fails_it_naming_both_sets(string change)
    {
        // 04-tests F-07, tier D, read per running task (#873): no restart, no new task definition, no deployment.
        RunningTask stranger = new("c0ffee00c0ff", Other.Revision, Other.StartedAt);
        RunningTask[] after = change switch
        {
            "replaced" => [Gateway, stranger],
            "restarted" => [Gateway, Other with { StartedAt = Other.StartedAt.AddMinutes(1) }],
            "redefined" => [Gateway, Other with { Revision = "spa-platform-poc:5" }],
            "gone" => [Gateway],
            "added" => [Gateway, Other, stranger],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "not a row of this theory"),
        };
        CellState cellState = new() { Tasks = [[Gateway, Other], after] };
        RecordedRunner runner = OnboardingRunner(cellState);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("F-07", refused.Message, StringComparison.Ordinal);
        Assert.All(new[] { Gateway, Other }.Concat(after), task => Assert.Contains(task.ToString(), refused.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_two_tasks_listed_in_the_other_order_after_the_onboarding_pass()
    {
        // The page for list-tasks names no order for taskArns: https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_ListTasks.html
        CellState cellState = new() { Tasks = [[Gateway, Other], [Other, Gateway]] };
        RecordedRunner runner = OnboardingRunner(cellState);

        Onboarded onboarded = await Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Equal(2, cellState.TasksDescribed);
        Assert.Equal(2, onboarded.Tasks.Count);
    }

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    /// <summary>Onboarding's runner: spactl's add changes the registry, and the plan shows the app's own resources.</summary>
    private static RecordedRunner OnboardingRunner(CellState cellState) => RecordedRunner.WithExits(command =>
    {
        if (command.Program == "spactl" && command.Arguments[0] == "app")
        {
            cellState.Add("app3");
        }

        return cellState.Answer(command, App3Resources.Select(address => (address, "create")).ToArray());
    });

    /// <summary>The clean-up's runner: spactl's remove changes the registry, and the plan destroys the app's own resources.</summary>
    private static RecordedRunner OffboardingRunner(CellState cellState) => RecordedRunner.WithExits(command => OffboardingAnswer(cellState, command));

    private static CommandResult OffboardingAnswer(CellState cellState, Command command) => command.Program == "spactl" && command.Arguments[1] == "remove"
        ? cellState.Removed("app3")
        : cellState.Answer(command, App3Resources.Select(address => (address, "delete")).ToArray());

    /// <summary>A first clean-up on this state that took the app out of the registry and stopped on its failed wait, having run no Terraform.</summary>
    private async Task FailedFirstCleanUpAsync(CellState cellState)
    {
        cellState.Add("app3");
        RecordedRunner runner = RecordedRunner.WithExits(command => command.Program == "spactl" && command.Arguments[0] == "wait"
            ? new CommandResult(1, "")
            : OffboardingAnswer(cellState, command));

        await Assert.ThrowsAsync<CommandFailedException>(() => Onboarding(runner, new Tunnel(runner)).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform");
    }

    /// <summary>
    /// The onboarding of a service with one defect, refused before Terraform or spactl ran. The plan
    /// would show the app's own resources, so a guard removed lets the recording go on to the apply and
    /// the test fails, rather than a later refusal answering for it.
    /// </summary>
    private async Task<InvalidOperationException> RefusedBeforeAnyPlanAsync(CellState cellState)
    {
        RecordedRunner runner = OnboardingRunner(cellState);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner, new Tunnel(runner)).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runner.Calls, call => call.Program is "terraform" or "spactl");
        Assert.Contains("wait", refused.Message, StringComparison.OrdinalIgnoreCase);
        return refused;
    }

    /// <summary><c>ecs describe-services</c>'s answer for the service with these deployments.</summary>
    private static string Service(params JsonObject[] deployments) => new JsonObject
    {
        ["services"] = new JsonArray(new JsonObject { ["serviceName"] = "gateway", ["deployments"] = new JsonArray(deployments) }),
        ["failures"] = new JsonArray(),
    }.ToJsonString();

    /// <summary>A deployment as <c>describe-services</c> gives it (https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Deployment.html); a null rollout state is one ECS left out.</summary>
    private static JsonObject Deployment(string status, string? rollout, int running, int desired)
    {
        JsonObject deployment = new() { ["status"] = status, ["runningCount"] = running, ["desiredCount"] = desired };
        if (rollout is not null)
        {
            deployment["rolloutState"] = rollout;
        }

        return deployment;
    }

    private CellOnboarding Onboarding(RecordedRunner runner, Tunnel tunnel)
    {
        Cell cell = Cell.Parse(Example);
        return new CellOnboarding(cell, new CellAws(cell, runner), runner, Setup(tunnel));
    }

    private OnboardingSetup Setup(Tunnel tunnel) => new(
        FixtureFiles.RepositoryRoot, appsRoot, outputsPath, "/config/platform.json", new Uri("https://app1.spa-poc.test:9443/"), ["spactl"], Project, tunnel.SendAsync);

    private static string Verb((string Program, string[] Arguments) call) =>
        call.Program == "terraform" ? call.Arguments[1] : call.Arguments[0];

    private static string[] Call(RecordedRunner runner, string program, string verb) =>
        runner.Calls.Last(call => call.Program == program && Verb(call) == verb).Arguments;

    private static string Option(string[] arguments, string name, bool inline = false) =>
        inline ? arguments.Single(argument => argument.StartsWith(name, StringComparison.Ordinal))[name.Length..] : arguments[Array.IndexOf(arguments, name) + 1];

    private static string Plan(params (string Address, string Actions)[] changes) =>
        TerraformPlanTests.Plan([.. changes.Select(change => (change.Address, "managed", change.Actions.Split(',')))]);

    /// <summary>
    /// The tunnel as the onboarding reaches it after the wait: by default app 3's host sends a login
    /// navigation to the cell's Cognito authorize endpoint with app 3's new client and a state, as the
    /// gateway's <c>/bff/login</c> does (contracts/http-contract.md).
    /// </summary>
    private sealed class Tunnel(RecordedRunner runner)
    {
        public int Status { get; init; } = 302;

        public string? Location { get; init; } =
            $"{Cell.Parse(Example).AuthorizeEndpoint}?client_id={NewClientId}&redirect_uri=https%3A%2F%2Fapp3.spa-poc.test%3A9443%2Fsignin-oidc&state={Canary}";

        public List<Sent> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Sent(request.RequestUri!, request.Headers.TryGetValues("Sec-Fetch-Mode", out IEnumerable<string>? mode) ? mode.Single() : null, runner.Calls.Count));
            HttpResponseMessage response = new((System.Net.HttpStatusCode)Status);
            if (Location is not null)
            {
                response.Headers.Location = new Uri(Location);
            }

            return Task.FromResult(response);
        }

        /// <summary>A request the tunnel saw, and how many commands had run before it.</summary>
        public sealed record Sent(Uri Url, string? FetchMode, int CallsBefore);
    }

    /// <summary>
    /// The cell as the runner answers for it: the live registry, which spactl's add and remove change,
    /// the gateway service's deployments and running tasks, the apps root's plan with its detailed exit,
    /// and its client outputs.
    /// </summary>
    private sealed class CellState
    {
        private readonly List<string> apps = ["app1", "app2"];
        private long revision = 7;

        /// <summary>The running set each read answers, in turn; the last one answers every later read.</summary>
        public IReadOnlyList<RunningTask[]> Tasks { get; init; } = [[Gateway]];

        /// <summary><c>describe-services</c>'s answer, when it is not the steady one the first set gives.</summary>
        public string? Service { get; init; }

        /// <summary>The plan's exit when it is not the changes' own: 1 is Terraform's error.</summary>
        public int? PlanExit { get; init; }

        public int TasksDescribed { get; private set; }

        public void Add(string appId)
        {
            apps.Add(appId);
            revision++;
        }

        public CommandResult Removed(string appId)
        {
            apps.Remove(appId);
            revision++;
            return new CommandResult(0, "");
        }

        public CommandResult Answer(Command command, params (string Address, string Actions)[] changes)
        {
            string[] arguments = [.. command.Arguments];
            return (command.Program, arguments[0]) switch
            {
                ("aws", "s3") => Ok(new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["revision"] = revision,
                    ["apps"] = new JsonArray([.. apps.Select(app => (JsonNode)new JsonObject { ["appId"] = app, ["release"] = "r" })]),
                }.ToJsonString()),
                ("aws", "ecs") when arguments[1] == "describe-services" => Ok(Service ?? CellOnboardingTests.Service(Deployment("PRIMARY", "COMPLETED", Tasks[0].Length, Tasks[0].Length))),
                ("aws", "ecs") when arguments[1] == "list-tasks" => Ok(new JsonObject
                {
                    ["taskArns"] = new JsonArray([.. Current.Select(task => (JsonNode)$"arn:aws:ecs:us-east-1:111122223333:task/spa-poc/{task.TaskId}")]),
                }.ToJsonString()),
                ("aws", "ecs") => Ok(Describe()),
                // https://developer.hashicorp.com/terraform/cli/commands/plan#detailed-exitcode: 0 no changes, 1 error, 2 changes present.
                ("terraform", _) when arguments[1] == "plan" => new CommandResult(PlanExit ?? (changes.Length > 0 ? 2 : 0), ""),
                ("terraform", _) when arguments[1] == "show" => Ok(Plan(changes)),
                ("terraform", _) when arguments[1] == "output" => Ok($$$"""
                    {"app1":{"clientId":"1example23456789","secretParameter":"/spa-platform/poc/apps/app1/client-secret","secretVersion":1},
                     "app3":{"clientId":"{{{NewClientId}}}","secretParameter":"/spa-platform/poc/apps/app3/client-secret","secretVersion":1}}
                    """),
                _ => Ok(""),
            };
        }

        private RunningTask[] Current => Tasks[Math.Min(TasksDescribed, Tasks.Count - 1)];

        private string Describe()
        {
            RunningTask[] tasks = Current;
            TasksDescribed++;
            return new JsonObject
            {
                ["tasks"] = new JsonArray([.. tasks.Select(task => (JsonNode)new JsonObject
                {
                    ["taskArn"] = $"arn:aws:ecs:us-east-1:111122223333:task/spa-poc/{task.TaskId}",
                    ["taskDefinitionArn"] = $"arn:aws:ecs:us-east-1:111122223333:task-definition/{task.Revision}",
                    ["startedAt"] = task.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["lastStatus"] = "RUNNING",
                    ["desiredStatus"] = "RUNNING",
                })]),
                ["failures"] = new JsonArray(),
            }.ToJsonString();
        }

        private static CommandResult Ok(string output) => new(0, output);
    }
}
