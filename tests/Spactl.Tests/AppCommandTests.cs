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

        string error = spactl.Error.ToString();
        Assert.Contains("no client for 'app2'", error, StringComparison.Ordinal);
        Assert.Contains("write the outputs document again", error, StringComparison.Ordinal);
        // The refusal serves any cell, so it names no Terraform root and no script (#1355).
        Assert.DoesNotContain("root", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terraform", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".sh", error, StringComparison.Ordinal);
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task Add_completes_the_entry_from_an_outputs_document_of_only_region_bucket_and_clients()
    {
        // contracts/infra-outputs.schema.json requires only the three fields spactl reads, so a cell
        // with its own identity provider and no reference scripts writes just these.
        JsonObject example = Harness.ExampleJson("infra-outputs.example.json");
        JsonObject outputs = new()
        {
            ["region"] = example["region"]!.DeepClone(),
            ["bucket"] = example["bucket"]!.DeepClone(),
            ["clients"] = new JsonObject { ["app1"] = App1Client.DeepClone() },
        };
        spactl.OutputsPath = spactl.WriteFile("three-field-outputs.json", outputs.ToJsonString());
        await Publish("app1", "r1");

        Assert.Equal(0, await spactl.Run("app", "add", App1Definition, "--release", "r1"));

        JsonObject entry = Assert.Single((await spactl.LiveRegistry())["apps"]!.AsArray())!.AsObject();
        Assert.Equal("app1", entry["appId"]!.GetValue<string>());
        Assert.Equal("r1", entry["release"]!.GetValue<string>());
        Assert.Equal(App1Client["clientId"]!.GetValue<string>(), entry["oauth"]!["clientId"]!.GetValue<string>());
        Assert.Equal(
            $"ssm:{App1Client["secretParameter"]!.GetValue<string>()}:{App1Client["secretVersion"]!.GetValue<int>()}",
            entry["oauth"]!["clientSecretRef"]!.GetValue<string>());
        Assert.Equal(example["bucket"]!.GetValue<string>(), spactl.Buckets[^1]);
    }

    [Fact]
    public async Task Add_without_outputs_is_refused_by_name_and_writes_nothing()
    {
        // No default path: a missing --outputs is never read from any cell's file (#1355). app add needs
        // the document for the client even when --bucket names the bucket.
        await Publish("app1", "r1");
        int written = spactl.Store.Written.Count;
        int stores = spactl.Buckets.Count;

        Assert.Equal(2, await spactl.RunRaw(
            "app", "add", App1Definition, "--release", "r1", "--config", spactl.ConfigPath,
            "--bucket", "named-bucket", "--region", "us-east-1"));

        Assert.Contains("--outputs", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(written, spactl.Store.Written.Count);
        Assert.Equal(stores, spactl.Buckets.Count);
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    /// <summary>Each store command, with every operand it needs and neither --outputs nor --bucket.</summary>
    public static TheoryData<string[]> StoreCommandsWithoutOutputs => new()
    {
        { ["registry", "apply", Harness.Example("registry.example.json"), "--config", Harness.Example("platform-config.example.json")] },
        { ["registry", "rollback", "--config", Harness.Example("platform-config.example.json")] },
        { ["registry", "validate", Harness.Example("registry.example.json"), "--config", Harness.Example("platform-config.example.json")] },
        { ["release", "publish", "app1", Harness.Example("spa", "app1"), "r2"] },
        { ["release", "activate", "app1", "r1", "--config", Harness.Example("platform-config.example.json")] },
        { ["app", "remove", "app1", "--config", Harness.Example("platform-config.example.json")] },
    };

    [Theory]
    [MemberData(nameof(StoreCommandsWithoutOutputs))]
    public async Task A_store_command_without_outputs_or_bucket_is_refused_by_name_and_writes_nothing(string[] args)
    {
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");
        int written = spactl.Store.Written.Count;
        int stores = spactl.Buckets.Count;

        Assert.Equal(2, await spactl.RunRaw(args));

        string error = spactl.Error.ToString();
        Assert.Contains("--outputs", error, StringComparison.Ordinal);
        Assert.Contains("--bucket with --region", error, StringComparison.Ordinal);
        Assert.Equal(written, spactl.Store.Written.Count);
        Assert.Equal(stores, spactl.Buckets.Count);
        Assert.Equal(1, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Release_activate_with_bucket_and_region_and_no_outputs_runs_against_that_bucket()
    {
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");
        await Publish("app1", "r2");

        Assert.Equal(0, await spactl.RunRaw(
            "release", "activate", "app1", "r2", "--config", spactl.ConfigPath,
            "--bucket", "named-bucket", "--region", "us-east-1"));

        Assert.Equal("named-bucket", spactl.Buckets[^1]);
        Assert.Equal("r2", (await spactl.LiveRegistry())["apps"]![0]!["release"]!.GetValue<string>());
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
