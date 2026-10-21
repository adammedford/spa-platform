using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Onboarding an app on the deployed cell while it serves the others, as F-07 does it (#212), in the
/// steps of docs/plan/05-infrastructure-and-cost.md -> Onboarding a third SPA: the apps root's plan
/// with the onboarding definition, read before it is applied and applied only when it adds that app's
/// own resources and nothing else; <c>spactl release publish</c> and <c>spactl app add</c>; and
/// <c>spactl wait</c> for the revision that names the app. <see cref="OffboardAsync"/> undoes it, so
/// a second sitting plans the same onboarding again.
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
        if (cell.HasClient(appId) || (await aws.LiveRegistryAsync(ct)).AppIds.Contains(appId))
        {
            throw new InvalidOperationException(
                $"{appId} is already onboarded on the cell, so the apps root's plan cannot show its onboarding. The run's clean-up takes it off; run again after it.");
        }

        RequireInitialised();
        string[] expected = PerAppAddresses(appId);
        string work = PrivateDirectory();
        try
        {
            string plan = Path.Combine(work, "apps.tfplan");
            IReadOnlyList<PlannedChange> planned = await PlanAsync(plan, ["-var", OnboardingDirectories], ct);
            Require(planned, [.. expected.Select(address => new PlannedChange(address, "create"))], "onboarding " + appId);

            await TerraformAsync(["apply", "-input=false", "-no-color", plan], ct);

            // The apps root's clients output is already in the outputs document's shape (infra/envs/poc/apps/README.md -> Outputs).
            JsonNode client = JsonNode.Parse(await TerraformAsync(["output", "-no-color", "-json", "clients"], ct))?[appId]
                ?? throw new InvalidOperationException($"the apps root applied and its clients output names no {appId}.");
            JsonObject outputs = JsonNode.Parse(await File.ReadAllTextAsync(setup.OutputsPath, ct))!.AsObject();
            outputs["clients"]![appId] = client.DeepClone();
            string outputsCopy = Path.Combine(work, "outputs.json");
            await File.WriteAllTextAsync(outputsCopy, outputs.ToJsonString(), ct);

            string release = ReleaseName(appId);
            await SpactlAsync(["release", "publish", appId, Path.Combine(setup.RepositoryRoot, "fixtures", "spa", appId), release, "--outputs", outputsCopy], ct);
            await SpactlAsync(["app", "add", Path.Combine(setup.RepositoryRoot, "fixtures", "onboarding", appId + ".json"), "--release", release,
                               "--config", setup.PlatformConfig, "--outputs", outputsCopy], ct);

            LiveRegistry registry = await aws.LiveRegistryAsync(ct);
            if (!registry.AppIds.Contains(appId))
            {
                throw new InvalidOperationException($"spactl app add returned and the live registry names no {appId}.");
            }

            await WaitAsync(registry.Revision, ct);
            return new Onboarded(cell.App(appId, client["clientId"]!.GetValue<string>()), planned, registry.Revision);
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
        if ((await aws.LiveRegistryAsync(ct)).AppIds.Contains(appId))
        {
            await SpactlAsync(["app", "remove", appId, "--config", setup.PlatformConfig, "--outputs", setup.OutputsPath], ct);
            await WaitAsync((await aws.LiveRegistryAsync(ct)).Revision, ct);
        }

        RequireInitialised();
        string[] owned = PerAppAddresses(appId);
        string work = PrivateDirectory();
        try
        {
            string plan = Path.Combine(work, "apps.tfplan");
            IReadOnlyList<PlannedChange> planned = await PlanAsync(plan, [], ct);
            if (planned.Count == 0)
            {
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

    private async Task<IReadOnlyList<PlannedChange>> PlanAsync(string plan, string[] variables, CancellationToken ct)
    {
        await TerraformAsync(["plan", "-input=false", "-no-color", "-out=" + plan, .. variables], ct);
        return TerraformPlan.Changes(await TerraformAsync(["show", "-json", "-no-color", plan], ct));
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

    private Task<string> TerraformAsync(string[] arguments, CancellationToken ct) =>
        runner.RunAsync("terraform", ["-chdir=" + setup.AppsRoot, .. arguments], ct);

    private Task<string> SpactlAsync(string[] arguments, CancellationToken ct) =>
        runner.RunAsync(setup.Spactl[0], [.. setup.Spactl.Skip(1), .. arguments], ct);

    private Task<string> WaitAsync(long revision, CancellationToken ct) =>
        SpactlAsync(["wait", "--url", setup.ReadyOrigin.ToString(), "--revision", revision.ToString(CultureInfo.InvariantCulture), "--timeout", WaitSeconds], ct);

    /// <summary>
    /// Named for the fixture's content, as seed.sh names the seed's: a release prefix is written once
    /// (ADR-0009) and spactl leaves identical bytes alone, so a second sitting's publish is a no-op and a
    /// changed fixture gets a new name rather than a refused overwrite.
    /// </summary>
    private static string ReleaseName(string appId)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] bytes) in FixtureFiles.Spa(appId).OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\0{bytes.Length}\0"));
            hash.AppendData(bytes);
        }

        return "onboarding-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }

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
/// <c>/readyz</c> on, and the spactl command line.
/// </summary>
public sealed record OnboardingSetup(string RepositoryRoot, string AppsRoot, string OutputsPath, string PlatformConfig, Uri ReadyOrigin, IReadOnlyList<string> Spactl);

/// <summary>An onboarded app, the plan read before it was applied, and the registry revision that named it.</summary>
public sealed record Onboarded(HarnessApp App, IReadOnlyList<PlannedChange> Planned, long Revision);
