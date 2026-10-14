using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl registry validate</c> and <c>registry apply</c> (plan row 2.6). Apply is conditional on the
/// live version (contracts/s3-layout.md: "PUT conditional on the live version"), validates with the one
/// library the gateway uses (ADR-0002), and checks that every entry's release prefix exists, which only
/// spactl does (contracts/README.md, "Rules that JSON Schema cannot express").
/// </summary>
public sealed class RegistryCommandTests : IDisposable
{
    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    /// <summary>contracts/registry.example.json, changed, written to a file.</summary>
    private string RegistryFile(Action<JsonObject>? change = null)
    {
        JsonObject document = Harness.ExampleJson("registry.example.json");
        change?.Invoke(document);
        return spactl.WriteFile("registry.json", document.ToJsonString());
    }

    private static string ExampleRelease => Harness.ExampleJson("registry.example.json")["apps"]![0]!["release"]!.GetValue<string>();

    private Task PublishExampleRelease() =>
        spactl.Store.SeedAsync($"apps/app1/releases/{ExampleRelease}/index.html", "<!doctype html>");

    [Fact]
    public async Task Validate_offline_accepts_the_contract_example()
    {
        Assert.Equal(0, await spactl.Run("registry", "validate", RegistryFile(), "--offline"));
        Assert.Contains("revision 42", spactl.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(spactl.Buckets);
    }

    [Fact]
    public async Task Validate_reports_each_rule_the_document_breaks_and_fails()
    {
        string file = RegistryFile(document => document["apps"]![0]!["host"] = "app1.elsewhere.test");

        Assert.Equal(1, await spactl.Run("registry", "validate", file, "--offline"));
        Assert.Contains("host-suffix-not-allowed", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_against_the_bucket_reports_a_release_prefix_that_does_not_exist()
    {
        Assert.Equal(1, await spactl.Run("registry", "validate", RegistryFile()));
        Assert.Contains($"apps/app1/releases/{ExampleRelease}/", spactl.Error.ToString(), StringComparison.Ordinal);

        await PublishExampleRelease();
        Assert.Equal(0, await spactl.Run("registry", "validate", RegistryFile()));
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task The_bucket_comes_from_the_infrastructure_outputs_unless_named()
    {
        await PublishExampleRelease();
        string bucket = Harness.ExampleJson("infra-outputs.example.json")["bucket"]!.GetValue<string>();

        await spactl.Run("registry", "validate", RegistryFile());
        await spactl.Run("registry", "validate", RegistryFile(), "--bucket", "other-bucket");

        Assert.Equal([bucket, "other-bucket"], spactl.Buckets);
    }

    [Fact]
    public async Task Apply_writes_the_first_registry_only_if_none_exists()
    {
        await PublishExampleRelease();

        Assert.Equal(0, await spactl.Run("registry", "apply", RegistryFile()));

        Assert.Equal(42, (await spactl.LiveRegistry())["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task Apply_refuses_a_revision_not_greater_than_the_live_one()
    {
        await PublishExampleRelease();
        await spactl.Run("registry", "apply", RegistryFile());

        Assert.Equal(1, await spactl.Run("registry", "apply", RegistryFile()));
        Assert.Contains("revision-not-greater", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Apply_replaces_the_live_registry_with_a_later_revision()
    {
        await PublishExampleRelease();
        await spactl.Run("registry", "apply", RegistryFile());

        Assert.Equal(0, await spactl.Run("registry", "apply", RegistryFile(document => document["revision"] = 43)));

        Assert.Equal(43, (await spactl.LiveRegistry())["revision"]!.GetValue<long>());
        Assert.Equal(2, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Apply_refuses_when_the_registry_appears_after_it_was_read()
    {
        // The conditional write is the store's: a registry written by someone else between spactl's read
        // and its write is not overwritten. Here the race is staged by a store that writes one first.
        await PublishExampleRelease();
        RacingStore racing = new(spactl.Store, Harness.RegistryKey, "{\"schemaVersion\":1,\"revision\":7,\"apps\":[]}");
        spactl.StoreOverride = racing;

        Assert.Equal(1, await spactl.Run("registry", "apply", RegistryFile()));
        Assert.Contains("changed", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(7, (await spactl.LiveRegistry())["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task Apply_refuses_when_the_live_registry_changes_after_it_was_read()
    {
        await PublishExampleRelease();
        await spactl.Run("registry", "apply", RegistryFile());
        spactl.StoreOverride = new RacingStore(spactl.Store, Harness.RegistryKey, "{\"schemaVersion\":1,\"revision\":50,\"apps\":[]}");

        Assert.Equal(1, await spactl.Run("registry", "apply", RegistryFile(document => document["revision"] = 43)));
        Assert.Equal(50, (await spactl.LiveRegistry())["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task Apply_refuses_a_document_whose_release_prefix_does_not_exist()
    {
        Assert.Equal(1, await spactl.Run("registry", "apply", RegistryFile()));
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task A_missing_platform_configuration_is_a_usage_error()
    {
        Assert.Equal(2, await spactl.RunRaw("registry", "validate", RegistryFile(), "--offline"));
        Assert.Contains("--config", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        Assert.Equal(2, await spactl.RunRaw("registry", "frobnicate"));
        Assert.Equal(2, await spactl.RunRaw());
    }
}
