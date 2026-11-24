using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl fragment publish</c> and <c>fragment activate</c>, and the fragment cases E1 to E6 of the
/// fragments spec's traces (#961; ticket #993). A fragment release is complete when its entry file
/// exists (contracts/s3-layout.md), and every registry write checks every fragment's entry file in the
/// document it writes.
/// </summary>
public sealed class FragmentCommandTests : IDisposable
{
    private const string NoEntryFile = "no entry file: publish it first";

    private readonly FragmentWorld world = new();

    private Harness Spactl => world.Spactl;

    private static string AppId => FragmentWorld.AppId;

    private static string FragmentId => FragmentWorld.FragmentId;

    private static string Ns => FragmentWorld.FragmentNamespace;

    public void Dispose() => world.Dispose();

    // ---- fragment publish -----------------------------------------------------------------------

    [Fact]
    public async Task E1_publish_reads_no_registry_claims_each_asset_first_and_writes_the_entry_last()
    {
        Dictionary<string, string> more = new()
        {
            ["assets/chunk.4d2a.js"] = "export const chunk = 1;",
            ["styles.css"] = "body { margin: 0 }",
        };

        Assert.Equal(0, await world.PublishFragment("f1", more: more));

        // No --config was passed (Harness.Run passes none to a publish) and no registry key was read.
        Assert.DoesNotContain(Harness.RegistryKey, Spactl.Store.Read);
        List<string> written = Spactl.Store.Written;
        Assert.Equal(
            [$"{Ns}claims/assets/chunk.4d2a.js", $"{Ns}releases/f1/assets/chunk.4d2a.js", $"{Ns}releases/f1/styles.css", $"{Ns}releases/f1/entry.js"],
            written);
    }

    [Theory]
    [InlineData("App1", "reports", "f1")]
    [InlineData("app1", "Reports", "f1")]
    [InlineData("app1", "ab", "f1")]
    [InlineData("app1", "../x", "f1")]
    [InlineData("app1", "reports", "f1/../../registry")]
    [InlineData("app1", "reports", "has space")]
    public async Task E1_publish_refuses_an_operand_the_registry_schema_refuses(string appId, string fragmentId, string release)
    {
        string directory = world.Directory("fragment", new Dictionary<string, string> { ["entry.js"] = "export {};" });

        Assert.Equal(2, await Spactl.Run("fragment", "publish", appId, fragmentId, directory, release));
        Assert.Empty(Spactl.Store.Written);
    }

    [Theory]
    [InlineData("assets/entry.js")]
    [InlineData("api/entry.js")]
    [InlineData("entry.mjs")]
    [InlineData(".entry.js")]
    [InlineData("-entry.js")]
    [InlineData("entry..js")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.js")]
    public async Task E1_publish_refuses_an_entry_outside_the_grammar(string entry)
    {
        string directory = world.Directory("fragment", new Dictionary<string, string> { [entry] = "export {};" });

        Assert.Equal(2, await Spactl.Run("fragment", "publish", AppId, FragmentId, directory, "f1", "--entry", entry));
        Assert.Empty(Spactl.Store.Written);
    }

    [Fact]
    public async Task E1_publish_accepts_an_entry_of_the_longest_length()
    {
        string entry = new string('a', PlatformPaths.EntryMaxLength - 3) + ".js";

        Assert.Equal(0, await world.PublishFragment("f1", entry));
        Assert.Equal($"{Ns}releases/f1/{entry}", Spactl.Store.Written[^1]);
    }

    [Fact]
    public async Task E1_publish_refuses_a_directory_without_its_entry_file_and_writes_nothing()
    {
        string directory = world.Directory("fragment", new Dictionary<string, string> { ["entry.js"] = "export {};", ["assets/a.js"] = "1" });

        Assert.Equal(1, await Spactl.Run("fragment", "publish", AppId, FragmentId, directory, "f1", "--entry", "main.js"));

        Assert.Contains("has no main.js", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Spactl.Store.Written);
    }

    [Theory]
    [InlineData("api/x.js")]
    [InlineData("API/x.js")]
    [InlineData("api")]
    public async Task E1_publish_refuses_a_top_level_api_and_writes_nothing(string reserved)
    {
        Assert.Equal(1, await world.PublishFragment("f1", more: new Dictionary<string, string> { [reserved] = "x" }));

        Assert.Contains("api/", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Spactl.Store.Written);
    }

    [Fact]
    public async Task Publish_accepts_an_api_directory_below_the_top_level()
    {
        Assert.Equal(0, await world.PublishFragment("f1", more: new Dictionary<string, string> { ["lib/api/x.js"] = "x" }));
        Assert.Contains($"{Ns}releases/f1/lib/api/x.js", Spactl.Store.Written);
    }

    // ---- app add with a fragment (E1) -----------------------------------------------------------

    [Fact]
    public async Task E1_app_add_with_a_fragment_release_checks_the_shell_and_the_entry_and_writes_schema_version_2()
    {
        await world.Onboard("r1", "f1");

        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal(2, live["schemaVersion"]!.GetValue<int>());
        JsonObject fragment = live["apps"]![0]!["fragments"]![0]!.AsObject();
        Assert.Equal("f1", fragment["release"]!.GetValue<string>());
        Assert.Contains($"apps/{AppId}/releases/r1/index.html", Spactl.Store.Read);
        Assert.Contains($"{Ns}releases/f1/entry.js", Spactl.Store.Read);
    }

    [Fact]
    public async Task E1_app_add_refuses_a_fragment_release_whose_entry_file_is_missing()
    {
        Assert.Equal(0, await world.PublishShell("r1"));

        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));

        Assert.Contains($"{Ns}releases/f1/entry.js", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains(NoEntryFile, Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task E1_app_add_refuses_a_shell_release_whose_index_html_is_missing()
    {
        Assert.Equal(0, await world.PublishFragment("f1"));

        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));

        Assert.Contains("has no index.html", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task E1_app_add_refuses_a_definition_the_validator_refuses()
    {
        await world.PublishShell("r1");
        await world.PublishFragment("f1");

        // registry.schema.json#/$defs/fragmentCsp: a fragment may not extend default-src.
        string definition = world.Definition(d => d["fragments"]![0]!["csp"] = new JsonObject { ["default-src"] = new JsonArray("https://x.spa-poc.test") });

        Assert.Equal(1, await Spactl.Run("app", "add", definition, "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    // ---- entry (E2, E6) -------------------------------------------------------------------------

    [Fact]
    public async Task E2_a_definition_naming_main_js_is_checked_at_main_js()
    {
        Assert.Equal(0, await world.PublishShell("r1"));
        Assert.Equal(0, await world.PublishFragment("f1", "main.js"));
        string definition = world.Definition(d => d["fragments"]![0]!["entry"] = "main.js");
        Assert.Equal(0, await Spactl.Run("app", "add", definition, "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));
        Assert.Contains($"{Ns}releases/f1/main.js", Spactl.Store.Read);

        // f2 is published with the default entry, so the definition's main.js is not there.
        Assert.Equal(0, await world.PublishFragment("f2"));
        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));
        Assert.Contains($"{Ns}releases/f2/main.js", Spactl.Error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await world.PublishFragment("f3", "main.js"));
        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f3"));
    }

    [Fact]
    public async Task E6_a_hashed_entry_name_is_refused_by_each_single_field_write()
    {
        // An entry name that changes per release cannot be activated by a write that changes one
        // field: fragment activate keeps the definition's entry, and app add keeps the live release.
        Assert.Equal(0, await world.PublishShell("r1"));
        Assert.Equal(0, await world.PublishFragment("f1", "entry.3f2a1b.js"));
        Assert.Equal(0, await Spactl.Run(
            "app", "add", world.Definition(d => d["fragments"]![0]!["entry"] = "entry.3f2a1b.js"),
            "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));
        Assert.Equal(0, await world.PublishFragment("f2", "entry.9c8d7e.js"));
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        // The release alone: f2 has no entry.3f2a1b.js.
        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));
        Assert.Contains(NoEntryFile, Spactl.Error.ToString(), StringComparison.Ordinal);

        // The entry alone: the live release f1 has no entry.9c8d7e.js.
        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(d => d["fragments"]![0]!["entry"] = "entry.9c8d7e.js")));
        Assert.Contains($"{Ns}releases/f1/entry.9c8d7e.js", Spactl.Error.ToString(), StringComparison.Ordinal);

        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));
    }

    // ---- fragment activate (E3, E4, E5) ---------------------------------------------------------

    [Fact]
    public async Task E4_activate_changes_only_the_fragment_release_and_reads_no_fragment_index_html()
    {
        await world.Onboard("r1", "f1");
        Assert.Equal(0, await world.PublishFragment("f2", more: new Dictionary<string, string> { ["index.html"] = "<script type=importmap>{}</script>" }));
        JsonObject before = await Spactl.LiveRegistry();
        Spactl.Store.Read.Clear();

        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));

        JsonObject after = await Spactl.LiveRegistry();
        Assert.Equal("f2", after["apps"]![0]!["fragments"]![0]!["release"]!.GetValue<string>());
        Assert.Equal(before["revision"]!.GetValue<long>() + 1, after["revision"]!.GetValue<long>());
        JsonObject expected = before.DeepClone().AsObject();
        expected["revision"] = after["revision"]!.DeepClone();
        expected["apps"]![0]!["fragments"]![0]!["release"] = "f2";
        Assert.True(JsonNode.DeepEquals(expected, after), after.ToJsonString());

        // The shell's map is re-proved and the entry checked; no fragment's index.html is read, though
        // f2 holds one that would be refused as a shell's.
        Assert.Contains($"apps/{AppId}/releases/r1/index.html", Spactl.Store.Read);
        Assert.Contains($"{Ns}releases/f2/entry.js", Spactl.Store.Read);
        Assert.DoesNotContain(Spactl.Store.Read, key => key.StartsWith($"apps/{AppId}/fragments/", StringComparison.Ordinal) && key.EndsWith("index.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task E4_activate_checks_every_fragment_entry_of_the_document_not_only_the_one_it_changes()
    {
        await world.Onboard("r1", "f1");
        Assert.Equal(0, await world.PublishFragment("f2"));

        // A second fragment whose live release has no entry file, written past spactl.
        JsonObject live = await Spactl.LiveRegistry();
        JsonObject second = live["apps"]![0]!["fragments"]![0]!.DeepClone().AsObject();
        second["fragmentId"] = "orders";
        second["oauth"]!["clientId"] = "3example45678901";
        second["release"] = "o1";
        live["apps"]![0]!["fragments"]!.AsArray().Add(second);
        await world.OverwriteRegistry(live);

        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));
        Assert.Contains($"apps/{AppId}/fragments/orders/releases/o1/entry.js", Spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task E4_activate_refuses_a_fragment_the_live_entry_lacks()
    {
        await world.Onboard("r1", "f1");
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, "orders", "f1"));

        Assert.Contains("no fragment 'orders'", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task E4_activate_refuses_an_app_the_registry_lacks_and_a_missing_registry()
    {
        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f1"));
        Assert.Contains("no live registry", Spactl.Error.ToString(), StringComparison.Ordinal);

        await world.Onboard("r1", "f1");
        Assert.Equal(1, await Spactl.Run("fragment", "activate", "app9", FragmentId, "f1"));
        Assert.Contains("app9", Spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task E4_a_lost_if_match_answers_run_the_command_again()
    {
        await world.Onboard("r1", "f1");
        Assert.Equal(0, await world.PublishFragment("f2"));
        JsonObject competing = await Spactl.LiveRegistry();
        competing["revision"] = competing["revision"]!.GetValue<long>() + 1;
        Spactl.StoreOverride = new RacingStore(Spactl.Store, Harness.RegistryKey, competing.ToJsonString());

        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));

        Assert.Contains("run the command again", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(competing, await Spactl.LiveRegistry()));
    }

    [Fact]
    public async Task E3_a_publish_stopped_before_the_entry_leaves_a_release_no_write_can_name()
    {
        await world.Onboard("r1", "f1");
        Dictionary<string, string> more = new() { ["assets/chunk.js"] = "export const v = 2;" };
        string entryKey = $"{Ns}releases/f2/entry.js";
        Spactl.StoreOverride = new StoppingStore(Spactl.Store, entryKey);
        await Assert.ThrowsAsync<IOException>(() => world.PublishFragment("f2", more: more));
        Spactl.StoreOverride = null;
        Assert.Contains($"{Ns}releases/f2/assets/chunk.js", Spactl.Store.Written);
        Assert.Null(await Spactl.Store.TextAsync(entryKey));
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));
        Assert.Contains(NoEntryFile, Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(), "--fragment-release", $"{FragmentId}=f2"));
        Assert.Contains(NoEntryFile, Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));

        // Other bytes behind the same names are refused: an asset by its claim, any other key as written once.
        Assert.Equal(1, await world.PublishFragment("f2", more: new Dictionary<string, string> { ["assets/chunk.js"] = "export const v = 3;" }));
        Assert.Contains("claimed by release f2", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync(entryKey));

        // The same bytes complete it, and then it can be activated.
        Assert.Equal(0, await world.PublishFragment("f2", more: more));
        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));
    }

    [Fact]
    public async Task E5_rollback_is_activate_of_the_earlier_release_and_passes_while_it_holds_the_entry()
    {
        await world.Onboard("r1", "f1");
        Assert.Equal(0, await world.PublishFragment("f2"));
        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f2"));

        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f1"));

        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal("f1", live["apps"]![0]!["fragments"]![0]!["release"]!.GetValue<string>());
        Assert.Equal("r1", live["apps"]![0]!["release"]!.GetValue<string>());
    }

    [Fact]
    public async Task Activate_of_the_live_release_writes_nothing()
    {
        await world.Onboard("r1", "f1");
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(0, await Spactl.Run("fragment", "activate", AppId, FragmentId, "f1"));
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));
    }

    // ---- offline --------------------------------------------------------------------------------

    [Fact]
    public async Task Validate_offline_says_it_ran_neither_the_entry_check_nor_the_import_map_check()
    {
        JsonObject document = Harness.ExampleJson("registry.example.json");

        Assert.Equal(0, await Spactl.Run("registry", "validate", Spactl.WriteFile("registry.json", document.ToJsonString()), "--offline"));

        string said = Spactl.Out.ToString();
        Assert.Contains("neither the entry file check nor the import-map check ran", said, StringComparison.Ordinal);
        Assert.Empty(Spactl.Store.Read);
    }

    [Fact]
    public async Task Validate_online_refuses_a_document_whose_fragment_entry_file_is_missing()
    {
        JsonObject document = Harness.ExampleJson("registry.example.json");
        JsonObject fragment = document["apps"]![0]!["fragments"]![0]!.AsObject();
        await Spactl.Store.SeedAsync($"apps/app1/releases/{document["apps"]![0]!["release"]}/index.html", "<!doctype html>");

        Assert.Equal(1, await Spactl.Run("registry", "validate", Spactl.WriteFile("registry.json", document.ToJsonString())));

        Assert.Contains($"releases/{fragment["release"]}/{fragment["entry"]}", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains(NoEntryFile, Spactl.Error.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>A store whose write of one key fails as a dropped connection would: a publish stopped part way.</summary>
internal sealed class StoppingStore(IObjectStore inner, string stopAt) : IObjectStore
{
    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => inner.GetAsync(key, ifNoneMatch, ct);

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        key == stopAt ? Task.FromException<bool>(new IOException("The connection was reset.")) : inner.PutAsync(key, body, ifMatch, ct);
}
