using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// F-07's procedure on the cell (#212; docs/plan/05-infrastructure-and-cost.md -> Onboarding a third
/// SPA), against a recorded runner standing in for Terraform, the AWS CLI and spactl: the apps root's
/// plan with the onboarding definition is read before it is applied and applied only when it adds the
/// app's own resources; spactl publishes the release, adds the app and waits for the revision; and the
/// clean-up takes the app out of the registry before its client and secret go.
/// </summary>
public sealed class CellOnboardingTests : IDisposable
{
    private const string NewClientId = "3example33333333";
    private const string Canary = "client-secret-canary-0b1c";

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
    public async Task Onboarding_reads_the_plan_applies_exactly_it_then_publishes_adds_and_waits_for_the_new_revision()
    {
        CellState cellState = new();
        string? outputsSpactlRead = null;
        RecordedRunner runner = new((program, arguments) =>
        {
            if (program == "spactl" && arguments[0] == "app" && arguments[1] == "add")
            {
                outputsSpactlRead = File.ReadAllText(Option(arguments, "--outputs"));
                cellState.Add("app3");
            }

            return cellState.Answer(program, arguments, Plan(App3Resources.Select(address => (address, "create")).ToArray()));
        });

        Onboarded onboarded = await Onboarding(runner).OnboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["aws s3", "terraform plan", "terraform show", "terraform apply", "terraform output", "spactl release", "spactl app", "aws s3", "spactl wait"],
            runner.Calls.Select(call => $"{call.Program} {Verb(call)}"));

        string[] plan = Call(runner, "terraform", "plan");
        Assert.Contains("""definition_dirs=["../../../../fixtures/apps","../../../../fixtures/onboarding"]""", plan);
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
        Assert.Equal(8, onboarded.Revision);
    }

    [Fact]
    public async Task A_plan_that_changes_anything_but_the_new_apps_resources_is_not_applied_and_the_refusal_shows_no_value()
    {
        CellState cellState = new();
        RecordedRunner runner = new((program, arguments) => cellState.Answer(program, arguments,
            Plan([.. App3Resources.Select(address => (address, "create")), ("aws_cognito_user_pool_client.app[\"app1\"]", "update")])));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner).OnboardAsync("app3", TestContext.Current.CancellationToken));

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
        RecordedRunner runner = new((program, arguments) => cellState.Answer(program, arguments, Plan()));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner).OnboardAsync("app3", TestContext.Current.CancellationToken));

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
        RecordedRunner runner = new((program, arguments) => new CellState().Answer(program, arguments, Plan()));
        CellOnboarding onboarding = new(cell, new CellAws(cell, runner), runner, new OnboardingSetup(
            FixtureFiles.RepositoryRoot, appsRoot, outputsPath, "/config/platform.json", new Uri("https://app1.spa-poc.test:9443/"), ["spactl"]));

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
        RecordedRunner runner = new((program, arguments) => cellState.Answer(program, arguments, Plan()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Onboarding(runner).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task An_apps_root_not_initialised_is_refused_naming_its_readme()
    {
        Directory.Delete(Path.Combine(appsRoot, ".terraform"));
        RecordedRunner runner = new((program, arguments) => new CellState().Answer(program, arguments, Plan()));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner).OnboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("README.md", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform");
    }

    [Fact]
    public async Task The_clean_up_removes_the_app_from_the_registry_and_waits_before_it_destroys_its_resources()
    {
        CellState cellState = new();
        cellState.Add("app3");
        RecordedRunner runner = new((program, arguments) =>
        {
            if (program == "spactl" && arguments[0] == "app" && arguments[1] == "remove")
            {
                cellState.Remove("app3");
            }

            return cellState.Answer(program, arguments, Plan(App3Resources.Select(address => (address, "delete")).ToArray()));
        });

        await Onboarding(runner).OffboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["aws s3", "spactl app", "aws s3", "spactl wait", "terraform plan", "terraform show", "terraform apply"],
            runner.Calls.Select(call => $"{call.Program} {Verb(call)}"));
        Assert.Equal(["app", "remove", "app3"], Call(runner, "spactl", "app")[..3]);
        Assert.Equal("9", Option(Call(runner, "spactl", "wait"), "--revision"));
        Assert.DoesNotContain(Call(runner, "terraform", "plan"), argument => argument.Contains("definition_dirs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clean_up_with_nothing_left_applies_nothing()
    {
        RecordedRunner runner = new((program, arguments) => new CellState().Answer(program, arguments, Plan()));

        await Onboarding(runner).OffboardAsync("app3", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(runner.Calls, call => call.Program == "spactl" || (call.Program == "terraform" && call.Arguments[1] == "apply"));
    }

    [Fact]
    public async Task A_clean_up_plan_that_would_destroy_anything_else_is_not_applied()
    {
        RecordedRunner runner = new((program, arguments) => new CellState().Answer(program, arguments,
            Plan([.. App3Resources.Select(address => (address, "delete")), ("aws_ssm_parameter.client_secret[\"app1\"]", "delete")])));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Onboarding(runner).OffboardAsync("app3", TestContext.Current.CancellationToken));

        Assert.Contains("aws_ssm_parameter.client_secret[\"app1\"] delete", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Program == "terraform" && call.Arguments[1] == "apply");
    }

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    private CellOnboarding Onboarding(RecordedRunner runner)
    {
        Cell cell = Cell.Parse(Example);
        return new CellOnboarding(cell, new CellAws(cell, runner), runner, new OnboardingSetup(
            FixtureFiles.RepositoryRoot, appsRoot, outputsPath, "/config/platform.json", new Uri("https://app1.spa-poc.test:9443/"), ["spactl"]));
    }

    private static string Verb((string Program, string[] Arguments) call) =>
        call.Program == "terraform" ? call.Arguments[1] : call.Arguments[0];

    private static string[] Call(RecordedRunner runner, string program, string verb) =>
        runner.Calls.Last(call => call.Program == program && Verb(call) == verb).Arguments;

    private static string Option(string[] arguments, string name, bool inline = false) =>
        inline ? arguments.Single(argument => argument.StartsWith(name, StringComparison.Ordinal))[name.Length..] : arguments[Array.IndexOf(arguments, name) + 1];

    private static string Plan(params (string Address, string Actions)[] changes) =>
        TerraformPlanTests.Plan([.. changes.Select(change => (change.Address, "managed", change.Actions.Split(',')))]);

    /// <summary>The cell as the runner answers for it: the live registry, which spactl's add and remove change, and the apps root's client outputs.</summary>
    private sealed class CellState
    {
        private readonly List<string> apps = ["app1", "app2"];
        private long revision = 7;

        public void Add(string appId)
        {
            apps.Add(appId);
            revision++;
        }

        public void Remove(string appId)
        {
            apps.Remove(appId);
            revision++;
        }

        public string Answer(string program, string[] arguments, string plan) => (program, arguments[0]) switch
        {
            ("aws", "s3") => new JsonObject
            {
                ["schemaVersion"] = 1,
                ["revision"] = revision,
                ["apps"] = new JsonArray([.. apps.Select(app => (JsonNode)new JsonObject { ["appId"] = app, ["release"] = "r" })]),
            }.ToJsonString(),
            ("terraform", _) when arguments[1] == "show" => plan,
            ("terraform", _) when arguments[1] == "output" => $$$"""
                {"app1":{"clientId":"1example23456789","secretParameter":"/spa-platform/poc/apps/app1/client-secret","secretVersion":1},
                 "app3":{"clientId":"{{{NewClientId}}}","secretParameter":"/spa-platform/poc/apps/app3/client-secret","secretVersion":1}}
                """,
            _ => "",
        };
    }
}
