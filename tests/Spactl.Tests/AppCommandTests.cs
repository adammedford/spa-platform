using System.Text.Json.Nodes;
using Spactl;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl app add</c> and <c>app remove</c> (plan row 2.6). <c>app add</c> completes a definition
/// from <c>fixtures/apps/</c> with the client id and secret reference in the infrastructure outputs
/// (contracts/infra-outputs.schema.json, <c>clients</c>) and applies the registry. <c>app validate</c>
/// runs <c>app add</c>'s definition check alone, offline (#1509).
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

    // ---- app validate (#1509) -------------------------------------------------------------------

    /// <summary>
    /// Runs spactl in a world where every store, history and HTTP call throws, so a command that reaches
    /// any of them fails the test. No outputs path is passed: the harness's is never named.
    /// </summary>
    private async Task<int> RunOffline(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        SpactlEnvironment environment = new()
        {
            Store = _ => throw new InvalidOperationException("app validate built a store."),
            History = _ => throw new InvalidOperationException("app validate built a registry history."),
            Http = new ThrowingHandler(),
            Out = output,
            Error = error,
        };
        int exit = await SpactlApp.RunAsync(args, environment, TestContext.Current.CancellationToken);
        offlineOut = output.ToString();
        offlineError = error.ToString();
        return exit;
    }

    private string offlineOut = "";

    private string offlineError = "";

    private string Definition(Action<JsonObject> change)
    {
        JsonObject definition = Harness.ExampleJson("apps", "app1.json");
        change(definition);
        return spactl.WriteFile("definition.json", definition.ToJsonString());
    }

    [Fact]
    public async Task Validate_accepts_a_definition_inside_the_bounds_and_names_its_appId_on_one_line()
    {
        Assert.Equal(0, await RunOffline("app", "validate", App1Definition, "--config", spactl.ConfigPath));

        string line = Assert.Single(offlineOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("app1", line, StringComparison.Ordinal);
        Assert.Contains("app add", line, StringComparison.Ordinal);

        // app1 names two upstreams under one token set, which the validator warns on: the warning goes
        // to stderr as app add writes it.
        Assert.NotEqual("", offlineError);
        Assert.All(offlineError.Split('\n', StringSplitOptions.RemoveEmptyEntries), warning => Assert.StartsWith("warning: ", warning, StringComparison.Ordinal));
        await Publish("app1", "r1");
        await spactl.Run("app", "add", App1Definition, "--release", "r1");
        // app add's definition check writes the same lines first; its registry check may warn again.
        Assert.StartsWith(offlineError, spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_reads_no_outputs_document_and_builds_no_store()
    {
        // RunOffline's store, history and HTTP factories throw, and it passes no --outputs: a
        // definition that app add would accept still passes with nothing but the definition and config.
        string directory = spactl.ScratchDirectory("no-outputs");
        string definition = Path.Combine(directory, "app1.json");
        File.Copy(App1Definition, definition);
        string config = Path.Combine(directory, "platform-config.json");
        File.Copy(spactl.ConfigPath, config);

        Assert.Equal(0, await RunOffline("app", "validate", definition, "--config", config));
        Assert.Equal(["app1.json", "platform-config.json"], Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Validate_refuses_an_upstream_outside_the_bounds_in_the_text_app_add_prints()
    {
        string path = Definition(d => d["apis"]![0]!["upstream"] = "http://127.0.0.1:5999");

        Assert.Equal(1, await RunOffline("app", "validate", path, "--config", spactl.ConfigPath));
        Assert.Contains("[upstream-not-allowed]", offlineError, StringComparison.Ordinal);
        Assert.Contains($"{path} is not a valid app definition", offlineError, StringComparison.Ordinal);
        Assert.Equal("", offlineOut);

        Assert.Equal(1, await spactl.Run("app", "add", path, "--release", "r1"));
        Assert.Equal(spactl.Error.ToString(), offlineError);
    }

    [Fact]
    public async Task Validate_refuses_an_issuer_the_configuration_does_not_name()
    {
        string path = Definition(d => d["oauth"]!["issuer"] = "an-issuer-not-configured");

        Assert.Equal(1, await RunOffline("app", "validate", path, "--config", spactl.ConfigPath));
        Assert.Contains("[unknown-issuer]", offlineError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_refuses_a_host_outside_the_allowed_suffixes()
    {
        string path = Definition(d => d["host"] = "app1.elsewhere.test:8443");

        Assert.Equal(1, await RunOffline("app", "validate", path, "--config", spactl.ConfigPath));
        Assert.Contains("[host-suffix-not-allowed]", offlineError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_without_config_is_a_usage_error()
    {
        Assert.Equal(2, await RunOffline("app", "validate", App1Definition));
        Assert.Contains("--config", offlineError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--outputs")]
    [InlineData("--bucket")]
    [InlineData("--region")]
    [InlineData("--release")]
    public async Task Validate_refuses_an_option_it_does_not_read(string option)
    {
        Assert.Equal(2, await RunOffline("app", "validate", App1Definition, "--config", spactl.ConfigPath, option, "x"));
        Assert.Equal("", offlineOut);
    }

    [Fact]
    public async Task Usage_lists_app_validate_and_spactl_with_no_arguments_prints_it()
    {
        Assert.Contains("spactl app validate      <definition.json> --config <platform-config.json>", SpactlApp.Usage, StringComparison.Ordinal);

        Assert.Equal(2, await RunOffline());
        Assert.Contains("spactl app validate", offlineError, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("app validate made a network call.");
    }
}
