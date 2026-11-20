using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl app add</c> on a definition with fragments (contracts/app-definition.schema.json, the
/// fragment definition; ticket #993): each fragment's client from the infrastructure outputs'
/// <c>clients.{appId}.fragments.{fragmentId}</c>, its release from <c>--fragment-release</c> or the
/// live entry, and <c>schemaVersion: 2</c> from the first fragment on.
/// </summary>
public sealed class AppAddFragmentTests : IDisposable
{
    private const string Second = "orders";

    private readonly FragmentWorld world = new();

    private Harness Spactl => world.Spactl;

    private static string FragmentId => FragmentWorld.FragmentId;

    private static JsonObject FragmentClient =>
        Harness.ExampleJson("infra-outputs.example.json")["clients"]![FragmentWorld.AppId]!["fragments"]![FragmentId]!.AsObject();

    public void Dispose() => world.Dispose();

    /// <summary>The example outputs with a client for a second fragment beside the first.</summary>
    private string OutputsWithSecondFragment()
    {
        JsonObject outputs = Harness.ExampleJson("infra-outputs.example.json");
        outputs["clients"]![FragmentWorld.AppId]!["fragments"]![Second] = new JsonObject
        {
            ["clientId"] = "3example45678901",
            ["secretParameter"] = $"/spa-platform/poc/apps/{FragmentWorld.AppId}/fragments/{Second}/client-secret",
            ["secretVersion"] = 4,
        };
        return Spactl.WriteFile("outputs-two.json", outputs.ToJsonString());
    }

    /// <summary>The example definition with a second fragment, a copy of the first under another id.</summary>
    private static void AddSecondFragment(JsonObject definition)
    {
        JsonObject second = definition["fragments"]![0]!.DeepClone().AsObject();
        second["fragmentId"] = Second;
        definition["fragments"]!.AsArray().Add(second);
    }

    private async Task PublishSecond(string release)
    {
        string directory = world.Directory(Second, new Dictionary<string, string> { ["entry.js"] = "export {};" });
        Assert.Equal(0, await Spactl.Run("fragment", "publish", FragmentWorld.AppId, Second, directory, release));
    }

    private static JsonObject FragmentOf(JsonObject live, string fragmentId) =>
        live["apps"]![0]!["fragments"]!.AsArray().Single(f => f!["fragmentId"]!.GetValue<string>() == fragmentId)!.AsObject();

    [Fact]
    public async Task Add_completes_each_fragment_client_from_the_outputs()
    {
        await world.Onboard("r1", "f1");

        JsonObject fragment = FragmentOf(await Spactl.LiveRegistry(), FragmentId);
        Assert.Equal(FragmentClient["clientId"]!.GetValue<string>(), fragment["oauth"]!["clientId"]!.GetValue<string>());
        Assert.Equal(
            $"ssm:{FragmentClient["secretParameter"]!.GetValue<string>()}:{FragmentClient["secretVersion"]!.GetValue<int>()}",
            fragment["oauth"]!["clientSecretRef"]!.GetValue<string>());

        // The fragment is the definition's plus exactly the three completed values.
        JsonObject withoutCompletion = fragment.DeepClone().AsObject();
        withoutCompletion.Remove("release");
        withoutCompletion["oauth"]!.AsObject().Remove("clientId");
        withoutCompletion["oauth"]!.AsObject().Remove("clientSecretRef");
        Assert.True(JsonNode.DeepEquals(FragmentWorld.DefinitionJson["fragments"]![0], withoutCompletion));
    }

    [Fact]
    public async Task Add_refuses_a_fragment_the_outputs_lack()
    {
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        JsonObject outputs = Harness.ExampleJson("infra-outputs.example.json");
        outputs["clients"]![FragmentWorld.AppId]!.AsObject().Remove("fragments");
        Spactl.OutputsPath = Spactl.WriteFile("outputs-none.json", outputs.ToJsonString());

        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));

        Assert.Contains($"no client for fragment '{FragmentId}'", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("clientSecretRef")]
    public async Task Add_refuses_a_fragment_definition_naming_a_client_field(string field)
    {
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        string definition = world.Definition(d => d["fragments"]![0]!["oauth"]![field] = "named-by-hand");

        Assert.Equal(1, await Spactl.Run("app", "add", definition, "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task Add_reads_fragment_releases_as_one_comma_list()
    {
        Spactl.OutputsPath = OutputsWithSecondFragment();
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        await PublishSecond("o1");

        Assert.Equal(0, await Spactl.Run(
            "app", "add", world.Definition(AddSecondFragment), "--release", "r1", "--fragment-release", $"{FragmentId}=f1,{Second}=o1"));

        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal("f1", FragmentOf(live, FragmentId)["release"]!.GetValue<string>());
        Assert.Equal("o1", FragmentOf(live, Second)["release"]!.GetValue<string>());
        Assert.Equal("ssm:/spa-platform/poc/apps/app1/fragments/orders/client-secret:4", FragmentOf(live, Second)["oauth"]!["clientSecretRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_keeps_a_live_fragment_release_it_is_not_told_to_change()
    {
        Spactl.OutputsPath = OutputsWithSecondFragment();
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        await PublishSecond("o1");
        await PublishSecond("o2");
        string definition = world.Definition(AddSecondFragment);
        Assert.Equal(0, await Spactl.Run("app", "add", definition, "--release", "r1", "--fragment-release", $"{FragmentId}=f1,{Second}=o1"));

        Assert.Equal(0, await Spactl.Run("app", "add", definition, "--fragment-release", $"{Second}=o2"));

        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal("f1", FragmentOf(live, FragmentId)["release"]!.GetValue<string>());
        Assert.Equal("o2", FragmentOf(live, Second)["release"]!.GetValue<string>());
        Assert.Equal("r1", live["apps"]![0]!["release"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_refuses_a_fragment_new_to_the_entry_without_its_release()
    {
        Spactl.OutputsPath = OutputsWithSecondFragment();
        await world.Onboard("r1", "f1");
        await PublishSecond("o1");
        int versions = Spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(AddSecondFragment)));

        Assert.Contains($"--fragment-release {Second}=<release>", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(versions, Spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Add_refuses_a_fragment_release_for_a_fragment_the_definition_lacks()
    {
        await world.PublishShell("r1");
        await world.PublishFragment("f1");

        Assert.Equal(1, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentId}=f1,{Second}=o1"));

        Assert.Contains($"'{Second}'", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Theory]
    [InlineData("reports")]
    [InlineData("reports=f1,")]
    [InlineData("reports=f1,reports=f2")]
    [InlineData("Reports=f1")]
    [InlineData("reports=has space")]
    public async Task Add_refuses_a_fragment_release_list_it_cannot_read(string listed)
    {
        Assert.Equal(2, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", listed));
        Assert.Empty(Spactl.Store.Written);
    }

    [Fact]
    public async Task Schema_version_rises_to_2_on_the_first_fragment_and_is_never_lowered()
    {
        string withoutFragments = world.Definition(d => d.Remove("fragments"));
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        Assert.Equal(0, await Spactl.Run("app", "add", withoutFragments, "--release", "r1"));
        Assert.Equal(1, (await Spactl.LiveRegistry())["schemaVersion"]!.GetValue<int>());

        Assert.Equal(0, await Spactl.Run("app", "add", world.Definition(), "--fragment-release", $"{FragmentId}=f1"));
        Assert.Equal(2, (await Spactl.LiveRegistry())["schemaVersion"]!.GetValue<int>());

        // The fragment removed again, by app add and by a rollback to the first document: still 2.
        Assert.Equal(0, await Spactl.Run("app", "add", withoutFragments));
        JsonObject live = await Spactl.LiveRegistry();
        Assert.Null(live["apps"]![0]!["fragments"]);
        Assert.Equal(2, live["schemaVersion"]!.GetValue<int>());

        Assert.Equal(0, await Spactl.Run("registry", "rollback", "--to-revision", "1"));
        Assert.Equal(2, (await Spactl.LiveRegistry())["schemaVersion"]!.GetValue<int>());

        Assert.Equal(0, await Spactl.Run("app", "remove", FragmentWorld.AppId));
        Assert.Equal(2, (await Spactl.LiveRegistry())["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_raw_apply_that_would_lower_the_schema_version_is_refused_and_writes_nothing()
    {
        // registry.schema.json#/properties/schemaVersion: "spactl writes 2 on a document's first fragment
        // and never lowers it". apply writes the operator's file as written, so it refuses rather than
        // rewrites a file that declares less than the live document.
        await world.PublishShell("r1");
        await world.PublishFragment("f1");
        Assert.Equal(0, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentId}=f1"));
        JsonObject live = await Spactl.LiveRegistry();
        Assert.Equal(2, live["schemaVersion"]!.GetValue<int>());

        JsonObject lowered = live.DeepClone().AsObject();
        lowered["schemaVersion"] = 1;
        lowered["revision"] = live["revision"]!.GetValue<long>() + 1;
        lowered["apps"]![0]!.AsObject().Remove("fragments");
        int written = Spactl.Store.Written.Count;

        Assert.Equal(1, await Spactl.Run("registry", "apply", Spactl.WriteFile("lowered.json", lowered.ToJsonString())));

        Assert.Contains("schemaVersion 1", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(written, Spactl.Store.Written.Count);
        Assert.Equal(2, (await Spactl.LiveRegistry())["schemaVersion"]!.GetValue<int>());

        lowered["schemaVersion"] = 2;
        Assert.Equal(0, await Spactl.Run("registry", "apply", Spactl.WriteFile("kept.json", lowered.ToJsonString())));
    }
}
