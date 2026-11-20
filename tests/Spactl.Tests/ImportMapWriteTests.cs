using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// G5.1 to G5.8 of the fragments spec's traces (#961; ticket #993): the import-map check on every
/// registry write, read against the <c>script-src</c> the gateway would send. Shell releases r1, r2 and
/// r3 each carry one map, m1, m2 and m3, whose hashes H1, H2 and H3 are computed here from the text.
/// </summary>
public sealed class ImportMapWriteTests : IDisposable
{
    private static readonly string M1 = FragmentWorld.Map("m1");
    private static readonly string M2 = FragmentWorld.Map("m2");
    private static readonly string M3 = FragmentWorld.Map("m3");
    private static readonly string H1 = FragmentWorld.HashSource(M1);
    private static readonly string H2 = FragmentWorld.HashSource(M2);
    private static readonly string H3 = FragmentWorld.HashSource(M3);

    private readonly FragmentWorld world = new();

    private Harness Spactl => world.Spactl;

    private static string AppId => FragmentWorld.AppId;

    private string Error => Spactl.Error.ToString();

    public void Dispose() => world.Dispose();

    private string Definition(params string[] scriptSrc) => world.Definition(d => FragmentWorld.ScriptSrc(d, scriptSrc));

    /// <summary>r1 and r2 published, the fragment at f1, and the entry live at r2 listing H1 and H2.</summary>
    private async Task LiveAtR2()
    {
        Assert.Equal(0, await world.PublishShell("r1", M1));
        Assert.Equal(0, await world.PublishShell("r2", M2));
        Assert.Equal(0, await world.PublishFragment("f1"));
        Assert.Equal(0, await Spactl.Run("app", "add", Definition(H1, H2), "--release", "r2", "--fragment-release", $"{FragmentWorld.FragmentId}=f1"));
    }

    private async Task<string> LiveRelease() => (await Spactl.LiveRegistry())["apps"]![0]!["release"]!.GetValue<string>();

    [Fact]
    public async Task G5_1_activating_a_release_whose_map_hash_is_listed_passes()
    {
        await LiveAtR2();

        Assert.Equal(0, await Spactl.Run("release", "activate", AppId, "r1"));
        Assert.Equal("r1", await LiveRelease());
    }

    [Fact]
    public async Task G5_2_a_new_map_needs_its_hash_listed_first_and_activates_after()
    {
        await LiveAtR2();
        Assert.Equal(0, await world.PublishShell("r3", M3));

        Assert.Equal(1, await Spactl.Run("release", "activate", AppId, "r3"));
        Assert.Contains(H3, Error, StringComparison.Ordinal);
        Assert.Equal("r2", await LiveRelease());

        Assert.Equal(0, await Spactl.Run("app", "add", Definition(H1, H2, H3)));
        Assert.Equal(0, await Spactl.Run("release", "activate", AppId, "r3"));
        Assert.Equal("r3", await LiveRelease());
    }

    [Fact]
    public async Task G5_3_app_add_dropping_the_live_release_hash_is_refused_and_the_live_document_is_unchanged()
    {
        await LiveAtR2();
        string before = (await Spactl.Store.TextAsync(Harness.RegistryKey))!;

        Assert.Equal(1, await Spactl.Run("app", "add", Definition(H1)));

        Assert.Contains(H2, Error, StringComparison.Ordinal);
        Assert.Equal(before, await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task G5_4_dropping_another_release_hash_warns_then_blocks_that_release_until_it_is_added_back()
    {
        await LiveAtR2();

        Assert.Equal(0, await Spactl.Run("app", "add", Definition(H2)));
        Assert.Contains("warning:", Error, StringComparison.Ordinal);
        Assert.Contains($"removes {H1}", Error, StringComparison.Ordinal);

        Assert.Equal(1, await Spactl.Run("release", "activate", AppId, "r1"));
        Assert.Contains(H1, Error, StringComparison.Ordinal);

        Assert.Equal(0, await Spactl.Run("app", "add", Definition(H1, H2)));
        Assert.DoesNotContain("removes", Error, StringComparison.Ordinal);
        Assert.Equal(0, await Spactl.Run("release", "activate", AppId, "r1"));
    }

    [Fact]
    public async Task G5_5_a_raw_apply_whose_release_and_csp_disagree_is_refused_before_the_write()
    {
        await LiveAtR2();
        JsonObject document = await Spactl.LiveRegistry();
        document["revision"] = document["revision"]!.GetValue<long>() + 1;
        document["apps"]![0]!["release"] = "r1";
        FragmentWorld.ScriptSrc(document["apps"]![0]!.AsObject(), H2);
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await Spactl.Run("registry", "apply", Spactl.WriteFile("registry.json", document.ToJsonString())));

        Assert.Contains(H1, Error, StringComparison.Ordinal);
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task G5_6_a_rollback_to_a_document_that_passed_passes_again()
    {
        await LiveAtR2();
        JsonObject passed = await Spactl.LiveRegistry();
        Assert.Equal(0, await Spactl.Run("app", "add", Definition(H2)));
        Spactl.Store.Read.Clear();

        Assert.Equal(0, await Spactl.Run("registry", "rollback"));

        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal("r2", live["apps"]![0]!["release"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(passed["apps"], live["apps"]));
        Assert.Contains($"apps/{AppId}/releases/r2/index.html", Spactl.Store.Read);
    }

    [Fact]
    public async Task G5_7_a_fragment_activation_leaves_script_src_unchanged_and_re_proves_the_shell_map()
    {
        await LiveAtR2();
        Assert.Equal(0, await world.PublishFragment("f2"));
        JsonObject before = await Spactl.LiveRegistry();
        Spactl.Store.Read.Clear();

        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentWorld.FragmentId, "f2"));

        // What ContentSecurityPolicy.Merge reads for script-src, the shell's csp and each fragment's,
        // is what it was.
        JsonObject after = await Spactl.LiveRegistry();
        Assert.True(JsonNode.DeepEquals(before["apps"]![0]!["csp"], after["apps"]![0]!["csp"]));
        Assert.Equal(
            before["apps"]![0]!["fragments"]!.AsArray().Select(f => f!["csp"]?.ToJsonString()),
            after["apps"]![0]!["fragments"]!.AsArray().Select(f => f!["csp"]?.ToJsonString()));
        Assert.Contains($"apps/{AppId}/releases/r2/index.html", Spactl.Store.Read);

        // The same activation over a live shell entry whose map is not admitted, written past spactl,
        // is refused: the shell's map is proved again, though the write changes only a fragment.
        FragmentWorld.ScriptSrc(after["apps"]![0]!.AsObject(), H1);
        await world.OverwriteRegistry(after);
        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentWorld.FragmentId, "f1"));
        Assert.Contains(H2, Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task G5_8_an_unrelated_write_re_checks_every_entry_and_a_violating_live_entry_blocks_it()
    {
        await LiveAtR2();
        JsonObject outputs = Harness.ExampleJson("infra-outputs.example.json");
        outputs["clients"]!["app2"] = new JsonObject
        {
            ["clientId"] = "2example23456789",
            ["secretParameter"] = "/spa-platform/poc/apps/app2/client-secret",
            ["secretVersion"] = 3,
        };
        Spactl.OutputsPath = Spactl.WriteFile("outputs-app2.json", outputs.ToJsonString());
        Assert.Equal(0, await Spactl.Run("release", "publish", "app2", Harness.Example("spa", "app2"), "a1"));
        Assert.Equal(0, await Spactl.Run("release", "publish", "app2", Harness.Example("spa", "app2"), "a2"));
        Assert.Equal(0, await Spactl.Run("app", "add", Harness.Example("apps", "app2.json"), "--release", "a1"));

        // app1's live entry stops admitting its own map, written past spactl.
        JsonObject live = await Spactl.LiveRegistry();
        FragmentWorld.ScriptSrc(live["apps"]![0]!.AsObject(), H1);
        await world.OverwriteRegistry(live);
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await Spactl.Run("release", "activate", "app2", "a2"));
        Assert.Contains(H2, Error, StringComparison.Ordinal);
        Assert.Equal(1, await Spactl.Run("app", "remove", "app2"));
        Assert.Contains(H2, Error, StringComparison.Ordinal);
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));

        // Removing the violating entry itself is a write that no longer carries it, so it passes.
        Assert.Equal(0, await Spactl.Run("app", "remove", AppId));
    }
}
