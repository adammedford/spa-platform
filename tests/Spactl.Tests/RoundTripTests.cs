using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// Plan row 2.6, "Done when": a full round trip against the in-memory store. Every command in turn, on
/// the repository's own documents (the fixture SPAs, <c>fixtures/apps/</c>, and the contract examples
/// of the platform configuration and the infrastructure outputs), in the order the runbooks use them
/// (docs/plan/05-infrastructure-and-cost.md, "Onboarding a third SPA" and "Deployment and rollback").
/// </summary>
public sealed class RoundTripTests : IDisposable
{
    private const string Gateway = "https://app1.spa-poc.test:8443";

    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    private async Task Step(int expectedExit, params string[] args)
    {
        int exit = await spactl.Run(args);
        Assert.True(exit == expectedExit, $"spactl {string.Join(' ', args)} exited {exit}: {spactl.Error}");
    }

    private async Task Wait(long revision)
    {
        int exit = await spactl.RunRaw("wait", "--url", Gateway, "--revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(exit == 0, $"wait --revision {revision} exited {exit}: {spactl.Error}");
    }

    [Fact]
    public async Task Every_command_in_turn_against_the_in_memory_store()
    {
        string spa = Harness.Example("spa", "app1");
        string definition = Harness.Example("apps", "app1.json");

        // Onboarding: publish, add, wait.
        await Step(0, "release", "publish", "app1", spa, "2026-10-10.1");
        await Step(0, "app", "add", definition, "--release", "2026-10-10.1");
        await Wait(1);

        // The registry it wrote is one the gateway's validator accepts, offline and against the bucket.
        string written = spactl.WriteFile("live.json", (await spactl.LiveRegistry()).ToJsonString());
        await Step(0, "registry", "validate", written, "--offline");

        // A new release: publish write-once, activate, wait. Publishing it again under the same name with
        // other bytes is refused.
        await Step(0, "release", "publish", "app1", spa, "2026-10-11.1");
        string changed = spactl.ScratchDirectory("changed");
        spactl.WriteFile("changed/index.html", "<!doctype html><p>other bytes</p>");
        await Step(1, "release", "publish", "app1", changed, "2026-10-11.1");
        await Step(0, "release", "activate", "app1", "2026-10-11.1");
        await Wait(2);

        // A registry edit through validate then apply, from the live document.
        JsonObject edited = await spactl.LiveRegistry();
        edited["revision"] = 3;
        edited["apps"]![0]!["session"]!["lifetimeMinutes"] = 60;
        string editedFile = spactl.WriteFile("edited.json", edited.ToJsonString());
        await Step(0, "registry", "validate", editedFile);
        await Step(0, "registry", "apply", editedFile);
        await Step(1, "registry", "apply", editedFile);
        await Wait(3);

        // Rollback: revision 2's content under revision 4.
        await Step(0, "registry", "rollback");
        await Wait(4);
        JsonObject rolledBack = await spactl.LiveRegistry();
        Assert.Equal(4, rolledBack["revision"]!.GetValue<long>());
        Assert.Equal(480, rolledBack["apps"]![0]!["session"]!["lifetimeMinutes"]!.GetValue<int>());
        Assert.Equal("2026-10-11.1", rolledBack["apps"]![0]!["release"]!.GetValue<string>());

        // Rolling the release back is activating the previous one.
        await Step(0, "release", "activate", "app1", "2026-10-10.1");
        await Wait(5);

        // Removal leaves a valid, empty registry.
        await Step(0, "app", "remove", "app1");
        await Wait(6);
        Assert.Empty((await spactl.LiveRegistry())["apps"]!.AsArray());

        // Every registry write is a version, and nothing under apps/ was written twice.
        Assert.Equal(6, spactl.Store.VersionCount(Harness.RegistryKey));
        Assert.All(
            spactl.Store.Written.Where(key => key.StartsWith("apps/", StringComparison.Ordinal)).GroupBy(key => key),
            group => Assert.Single(group));
    }
}
