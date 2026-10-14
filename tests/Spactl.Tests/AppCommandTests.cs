using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl app add</c> and <c>app remove</c> (plan row 2.6). <c>app add</c> completes a definition
/// from <c>fixtures/apps/</c> with the client id and secret reference in the infrastructure outputs
/// (contracts/infra-outputs.schema.json, <c>clients</c>) and applies the registry.
/// </summary>
public sealed class AppCommandTests : IDisposable
{
    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    private static string App1Definition => Harness.Example("apps", "app1.json");

    private static JsonObject App1Client => Harness.ExampleJson("infra-outputs.example.json")["clients"]!["app1"]!.AsObject();

    private Task Publish(string appId, string release) =>
        spactl.Run("release", "publish", appId, Harness.Example("spa", appId), release);

    /// <summary>The example outputs with a client for app2 beside app1's, written to a file.</summary>
    private string OutputsWithApp2(int app1SecretVersion = 1)
    {
        JsonObject outputs = Harness.ExampleJson("infra-outputs.example.json");
        outputs["clients"]!["app1"]!["secretVersion"] = app1SecretVersion;
        outputs["clients"]!["app2"] = new JsonObject
        {
            ["clientId"] = "2example23456789",
            ["secretParameter"] = "/spa-platform/poc/apps/app2/client-secret",
            ["secretVersion"] = 3,
        };
        return spactl.WriteFile("outputs.json", outputs.ToJsonString());
    }

    [Fact]
    public async Task Add_completes_the_definition_from_the_outputs_and_writes_the_first_registry()
    {
        await Publish("app1", "r1");

        Assert.Equal(0, await spactl.Run("app", "add", App1Definition, "--release", "r1"));

        JsonObject live = await spactl.LiveRegistry();
        Assert.Equal(1, live["revision"]!.GetValue<long>());
        JsonObject entry = Assert.Single(live["apps"]!.AsArray())!.AsObject();
        Assert.Equal("r1", entry["release"]!.GetValue<string>());
        Assert.Equal(App1Client["clientId"]!.GetValue<string>(), entry["oauth"]!["clientId"]!.GetValue<string>());
        Assert.Equal(
            $"ssm:{App1Client["secretParameter"]!.GetValue<string>()}:{App1Client["secretVersion"]!.GetValue<int>()}",
            entry["oauth"]!["clientSecretRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_carries_every_property_of_the_definition_into_the_entry()
    {
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");

        JsonObject definition = Harness.ExampleJson("apps", "app1.json");
        JsonObject entry = (await spactl.LiveRegistry())["apps"]![0]!.AsObject();

        // The entry is the definition plus exactly the three values a definition must not carry
        // (app-definition.schema.json's description).
        JsonObject withoutCompletion = entry.DeepClone().AsObject();
        withoutCompletion.Remove("release");
        withoutCompletion["oauth"]!.AsObject().Remove("clientId");
        withoutCompletion["oauth"]!.AsObject().Remove("clientSecretRef");
        Assert.True(JsonNode.DeepEquals(definition, withoutCompletion));
    }

    [Fact]
    public async Task Add_a_second_app_raises_the_revision_and_keeps_the_first()
    {
        spactl.OutputsPath = OutputsWithApp2();
        await Publish("app1", "r1");
        await Publish("app2", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");

        Assert.Equal(0, await spactl.Run("app", "add", Harness.Example("apps", "app2.json"), "--release", "r1"));

        JsonObject live = await spactl.LiveRegistry();
        Assert.Equal(2, live["revision"]!.GetValue<long>());
        Assert.Equal(["app1", "app2"], live["apps"]!.AsArray().Select(app => app!["appId"]!.GetValue<string>()));
        Assert.Equal("ssm:/spa-platform/poc/apps/app2/client-secret:3", live["apps"]![1]!["oauth"]!["clientSecretRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_again_after_a_secret_rotation_replaces_the_entry_and_keeps_its_release()
    {
        // docs/plan/05-infrastructure-and-cost.md, "Rotating a client secret", step 3.
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");
        spactl.OutputsPath = OutputsWithApp2(app1SecretVersion: 2);

        Assert.Equal(0, await spactl.Run("app", "add", App1Definition));

        JsonObject entry = Assert.Single((await spactl.LiveRegistry())["apps"]!.AsArray())!.AsObject();
        Assert.Equal("r1", entry["release"]!.GetValue<string>());
        Assert.EndsWith(":2", entry["oauth"]!["clientSecretRef"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_refuses_an_app_the_outputs_have_no_client_for()
    {
        await Publish("app2", "r1");

        Assert.Equal(1, await spactl.Run("app", "add", Harness.Example("apps", "app2.json"), "--release", "r1"));

        Assert.Contains("no client for 'app2'", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task Add_refuses_a_new_app_without_a_release()
    {
        Assert.Equal(1, await spactl.Run("app", "add", App1Definition));
        Assert.Contains("--release", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_refuses_a_definition_that_names_its_own_client_id()
    {
        // app-definition.schema.json: "Naming either here is rejected, so a definition can never carry a
        // client id that contradicts the one Terraform made."
        await Publish("app1", "r1");
        JsonObject definition = Harness.ExampleJson("apps", "app1.json");
        definition["oauth"]!["clientId"] = "a-client-terraform-did-not-make";

        Assert.Equal(1, await spactl.Run("app", "add", spactl.WriteFile("app1.json", definition.ToJsonString()), "--release", "r1"));
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task Add_refuses_infrastructure_outputs_that_break_their_schema()
    {
        await Publish("app1", "r1");
        JsonObject outputs = Harness.ExampleJson("infra-outputs.example.json");
        outputs["clients"]!["app1"]!["secretVersion"] = 0;
        spactl.OutputsPath = spactl.WriteFile("bad-outputs.json", outputs.ToJsonString());

        Assert.Equal(1, await spactl.Run("app", "add", App1Definition, "--release", "r1", "--bucket", "named-bucket"));
        Assert.Contains("infrastructure outputs", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_takes_the_entry_out_under_the_next_revision()
    {
        spactl.OutputsPath = OutputsWithApp2();
        await Publish("app1", "r1");
        await Publish("app2", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app2.json"), "--release", "r1");

        Assert.Equal(0, await spactl.Run("app", "remove", "app1"));

        JsonObject live = await spactl.LiveRegistry();
        Assert.Equal(3, live["revision"]!.GetValue<long>());
        Assert.Equal("app2", Assert.Single(live["apps"]!.AsArray())!["appId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Remove_refuses_an_app_the_registry_does_not_hold()
    {
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");

        Assert.Equal(1, await spactl.Run("app", "remove", "app2"));
        Assert.Equal(1, spactl.Store.VersionCount(Harness.RegistryKey));
    }
}
