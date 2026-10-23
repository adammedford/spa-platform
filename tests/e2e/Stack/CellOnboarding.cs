using System.Globalization;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Onboarding an app on the deployed cell while it serves the others, as F-07 does it (#212), in the
/// steps of docs/plan/05-infrastructure-and-cost.md -> Onboarding a third SPA: the apps root's plan
/// with the onboarding definition, read before it is applied and applied only when it adds that app's
/// own resources and nothing else; <c>spactl release publish</c> and <c>spactl app add</c>;
/// <c>spactl wait</c> for the revision that names the app; and the app answering through the tunnel
/// with a login at its own client. The gateway's running task is read before the plan and after the
/// app answers, and must be the same task on the same task definition revision (04-tests F-07, tier
/// D: no restart, no new task definition, no deployment). Terraform and spactl run as the project's
/// profile (<see cref="AwsProject"/>), whatever profile the shell had. <see cref="OffboardAsync"/>
/// undoes it, so a second sitting plans the same onboarding again.
///
/// <para>The onboarding definition is planned through the root's <c>definition_dirs</c> variable, as
/// its own P-02 test plans it (infra/envs/poc/apps/tests/root.tftest.hcl), rather than copied into
/// <c>fixtures/apps/</c>, which is workstream 5's and tracked: the run changes no file in the
/// repository. For the same reason spactl reads app 3's new client from a copy of the outputs
/// document in the run's own directory: <c>infra/envs/poc/outputs.json</c> is written by workstream
/// 1's scripts (contracts/infra-outputs.schema.json), and the clean-up leaves it true again.</para>
///
/// <para>Every saved plan of the applied root holds every client secret in cleartext
/// (infra/envs/poc/apps/README.md), so the plan lives in a directory only this user can read, outside
/// the repository, deleted before the call returns, and only addresses and actions are kept of it
/// (<see cref="TerraformPlan"/>).</para>
/// </summary>
public sealed class CellOnboarding(Cell cell, CellAws aws, ICommandRunner runner, OnboardingSetup setup)
{
    /// <summary>How long <c>spactl wait</c> may take for the revision: well past one registry poll.</summary>
    private const string WaitSeconds = "300";

    public async Task<Onboarded> OnboardAsync(string appId, CancellationToken ct)
    {
        if (cell.HasClient(appId))
        {
            // The clean-up never writes the outputs document (workstream 1's scripts do), so it cannot make this pass.
            throw new InvalidOperationException(
                $"the infrastructure outputs document already names a client for {appId}, so the apps root's plan cannot show its onboarding. Once the clean-up has taken {appId} off, write the document again from the roots' outputs, as tools/scripts/seed.sh's first step does, then run again.");
        }

        if ((await aws.LiveRegistryAsync(ct)).AppIds.Contains(appId))
        {
            throw new InvalidOperationException(
                $"{appId} is already in the live registry, so the apps root's plan cannot show its onboarding. The run's clean-up takes it off; run again after it.");
        }

        RequireInitialised();
        string[] expected = PerAppAddresses(appId);
        RunningTask before = await aws.RunningTaskAsync(ct);
        string work = PrivateDirectory();
        try
        {
            string plan = Path.Combine(work, "apps.tfplan");
            IReadOnlyList<PlannedChange> planned = await PlanAsync(plan, ["-var", OnboardingDirectories], ct);
            if (planned.Count == 0)
            {
                throw new InvalidOperationException(
                    $"the apps root's plan with the onboarding definition shows no change (terraform plan -detailed-exitcode exited 0), so it cannot show {appId}'s onboarding and nothing was applied.");
            }

            Require(planned, [.. expected.Select(address => new PlannedChange(address, "create"))], "onboarding " + appId);

            await TerraformAsync(["apply", "-input=false", "-no-color", plan], ct);

            // The apps root's clients output is already in the outputs document's shape (infra/envs/poc/apps/README.md -> Outputs).
            JsonNode client = JsonNode.Parse(await TerraformAsync(["output", "-no-color", "-json", "clients"], ct))?[appId]
                ?? throw new InvalidOperationException($"the apps root applied and its clients output names no {appId}.");
            JsonObject outputs = JsonNode.Parse(await File.ReadAllTextAsync(setup.OutputsPath, ct))!.AsObject();
            outputs["clients"]![appId] = client.DeepClone();
            string outputsCopy = Path.Combine(work, "outputs.json");
            await File.WriteAllTextAsync(outputsCopy, outputs.ToJsonString(), ct);

            string release = ContentRelease.Name("onboarding", FixtureFiles.Spa(appId));
            await SpactlAsync(["release", "publish", appId, Path.Combine(setup.RepositoryRoot, "fixtures", "spa", appId), release, "--outputs", outputsCopy], ct);
            await SpactlAsync(["app", "add", Path.Combine(setup.RepositoryRoot, "fixtures", "onboarding", appId + ".json"), "--release", release,
                               "--config", setup.PlatformConfig, "--outputs", outputsCopy], ct);

            LiveRegistry registry = await aws.LiveRegistryAsync(ct);
            if (!registry.AppIds.Contains(appId))
            {
                throw new InvalidOperationException($"spactl app add returned and the live registry names no {appId}.");
            }

            await WaitAsync(registry.Revision, ct);
            HarnessApp app = cell.App(appId, client["clientId"]!.GetValue<string>());
            await AnswersAsync(app, ct);

            RunningTask after = await aws.RunningTaskAsync(ct);
            if (after != before)
            {
                throw new InvalidOperationException(
                    $"the gateway's task changed while {appId} was onboarded, which F-07 says it must not: before {before}, after {after}.");
            }

            return new Onboarded(app, planned, registry.Revision, before);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// Takes the app off the cell, whatever part of <see cref="OnboardAsync"/> ran: out of the registry
    /// first, and only once the gateway serves that revision, the client, branding style and secret,
    /// so no snapshot names a secret that is gone. The apps root's plan without the onboarding
    /// definition is applied only when it destroys that app's resources and nothing else. The release
    /// stays in the bucket, since a release is written once (ADR-0009); teardown empties the bucket.
    /// </summary>
    public async Task OffboardAsync(string appId, CancellationToken ct)
    {
        // Before the registry changes, so a root that cannot destroy the app's resources leaves the app served rather than half taken off.
        RequireInitialised();
        if ((await aws.LiveRegistryAsync(ct)).AppIds.Contains(appId))
        {
            await SpactlAsync(["app", "remove", appId, "--config", setup.PlatformConfig, "--outputs", setup.OutputsPath], ct);
            await WaitAsync((await aws.LiveRegistryAsync(ct)).Revision, ct);
        }

        string[] owned = PerAppAddresses(appId);
        string work = PrivateDirectory();
        try
        {
            string plan = Path.Combine(work, "apps.tfplan");
            IReadOnlyList<PlannedChange> planned = await PlanAsync(plan, [], ct);
            if (planned.Count == 0)
            {
                // Nothing of the app is left to destroy: its resources never came, or an earlier clean-up took them.
                return;
            }

            Require(planned, [.. owned.Select(address => new PlannedChange(address, "delete")).Where(planned.Contains)], "taking " + appId + " off");
            await TerraformAsync(["apply", "-input=false", "-no-color", plan], ct);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The root's own definitions directory and the onboarding one, in the form root.tftest.hcl's P-02 run gives them.</summary>
    private const string OnboardingDirectories = """definition_dirs=["../../../../fixtures/apps","../../../../fixtures/onboarding"]""";

    /// <summary>
    /// The root's plan, saved to <paramref name="plan"/>, and its changes. Under <c>-detailed-exitcode</c>
    /// Terraform exits 0 for no changes, 2 for changes present and 1 for an error
    /// (https://developer.hashicorp.com/terraform/cli/commands/plan#detailed-exitcode), so an empty plan
    /// is read from the exit and never shown, and an error stays a failure rather than an empty plan.
    /// </summary>
    private async Task<IReadOnlyList<PlannedChange>> PlanAsync(string plan, string[] variables, CancellationToken ct)
    {
        CommandResult planned = await runner.RunAsync(
            Terraform(["plan", "-input=false", "-no-color", "-detailed-exitcode", "-out=" + plan, .. variables]) with { AcceptedExits = [0, 2] }, ct);
        return planned.ExitCode == 0 ? [] : TerraformPlan.Changes(await TerraformAsync(["show", "-json", "-no-color", plan], ct));
    }

    /// <summary>
    /// Whether the app answers through the tunnel: a login navigation on its host
    /// (contracts/request-gate.md, <c>/bff/login</c>) is sent on to the cell's authorize endpoint with
    /// the app's own client (contracts/http-contract.md). An unknown host would be 404 (N-08). The
    /// redirect carries the login's state, so only its status and endpoint are ever named.
    /// </summary>
    private async Task AnswersAsync(HarnessApp app, CancellationToken ct)
    {
        using HttpRequestMessage login = new(HttpMethod.Get, app.Url("/bff/login"));
        login.Headers.Add("Sec-Fetch-Mode", "navigate");
        login.Headers.Add("Sec-Fetch-Dest", "document");
        login.Headers.Add("Sec-Fetch-Site", "none");
        using HttpResponseMessage answer = await setup.Tunnel(login, ct);

        Uri? location = answer.Headers.Location;
        bool ownClient = (int)answer.StatusCode is >= 300 and < 400
            && location is { IsAbsoluteUri: true }
            && location.GetLeftPart(UriPartial.Path) == cell.AuthorizeEndpoint
            && QueryHelpers.ParseQuery(location.Query).TryGetValue("client_id", out Microsoft.Extensions.Primitives.StringValues clientId)
            && clientId == app.ClientId;
        if (!ownClient)
        {
            string sent = location is { IsAbsoluteUri: true } ? "to " + location.GetLeftPart(UriPartial.Path) : "nowhere";
            throw new InvalidOperationException(
                $"after spactl wait, {app.AppId} did not answer a login through the tunnel by sending it to {cell.AuthorizeEndpoint} with its own client {app.ClientId}: it answered {(int)answer.StatusCode}, sending it {sent}.");
        }
    }

    /// <summary>Refuses, naming addresses and actions only, a plan whose changes are not exactly <paramref name="expected"/>.</summary>
    private static void Require(IReadOnlyList<PlannedChange> planned, PlannedChange[] expected, string what)
    {
        if (planned.Count == 0 || !Sorted(planned).SequenceEqual(Sorted(expected)))
        {
            throw new InvalidOperationException(
                $"the apps root's plan for {what} is not that app's resources alone, so it was not applied. It plans: {string.Join("; ", planned)}. Expected: {string.Join("; ", expected)}.");
        }
    }

    private static IEnumerable<string> Sorted(IEnumerable<PlannedChange> changes) =>
        changes.Select(change => change.ToString()).Order(StringComparer.Ordinal);

    private string[] PerAppAddresses(string appId) =>
        TerraformPlan.PerAppAddresses(File.ReadAllText(Path.Combine(setup.AppsRoot, "main.tf")), appId);

    private void RequireInitialised()
    {
        if (!Directory.Exists(Path.Combine(setup.AppsRoot, ".terraform")))
        {
            throw new InvalidOperationException(
                $"the apps root at {setup.AppsRoot} is not initialised: run its init as infra/envs/poc/apps/README.md -> Applying it gives it, then run again.");
        }
    }

    private Command Terraform(string[] arguments) =>
        new("terraform", ["-chdir=" + setup.AppsRoot, .. arguments]) { Environment = setup.Project };

    private async Task<string> TerraformAsync(string[] arguments, CancellationToken ct) =>
        (await runner.RunAsync(Terraform(arguments), ct)).Output;

    private async Task<string> SpactlAsync(string[] arguments, CancellationToken ct) =>
        (await runner.RunAsync(new Command(setup.Spactl[0], [.. setup.Spactl.Skip(1), .. arguments]) { Environment = setup.Project }, ct)).Output;

    private Task<string> WaitAsync(long revision, CancellationToken ct) =>
        SpactlAsync(["wait", "--url", setup.ReadyOrigin.ToString(), "--revision", revision.ToString(CultureInfo.InvariantCulture), "--timeout", WaitSeconds], ct);

    /// <summary>A new directory outside the repository that only this user can open.</summary>
    private static string PrivateDirectory()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("spa-e2e-onboarding-");
        if (!OperatingSystem.IsWindows())
        {
            directory.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        return directory.FullName;
    }
}

/// <summary>
/// Where the onboarding runs: the repository (its fixtures), the apps root, the outputs document, the
/// platform configuration spactl bounds the registry with, the gateway origin <c>spactl wait</c> reads
/// <c>/readyz</c> on, the spactl command line, the project's AWS variables Terraform and spactl run
/// with (<see cref="AwsProject"/>), and the way through the tunnel to the cell's load balancer.
/// </summary>
public sealed record OnboardingSetup(
    string RepositoryRoot,
    string AppsRoot,
    string OutputsPath,
    string PlatformConfig,
    Uri ReadyOrigin,
    IReadOnlyList<string> Spactl,
    IReadOnlyDictionary<string, string> Project,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Tunnel);

/// <summary>An onboarded app, the plan read before it was applied, the registry revision that named it, and the gateway task that served throughout.</summary>
public sealed record Onboarded(HarnessApp App, IReadOnlyList<PlannedChange> Planned, long Revision, RunningTask Gateway);
