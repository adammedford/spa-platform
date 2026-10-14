using System.Text.Json.Nodes;

namespace Platform.Registry.Tests;

/// <summary>
/// The contract checks <c>spactl</c> (task 2.6) makes outside a registry document: the infrastructure
/// outputs it completes an app definition from, the platform configuration it reads the policy from,
/// and the two names it builds a release prefix out of. Each is evaluated against the schemas loaded
/// into one registry (contracts/README.md, "Using the schemas"), so the passing case is the contract's
/// own example and the failing case is that example with one rule broken.
/// </summary>
public sealed class ContractDocumentsTests
{
    private static string InfraOutputs(Action<JsonObject>? change = null)
    {
        JsonObject document = Examples.Json("infra-outputs.example.json");
        change?.Invoke(document);
        return document.ToJsonString();
    }

    [Fact]
    public void The_example_infrastructure_outputs_pass()
    {
        Assert.Empty(ContractDocuments.ValidateInfraOutputs(InfraOutputs()));
    }

    [Fact]
    public void Infrastructure_outputs_with_a_secret_value_beside_the_reference_fail()
    {
        // infra-outputs.schema.json closes each client: the parameter name, never the value.
        string document = InfraOutputs(outputs => outputs["clients"]!["app1"]!["secret"] = "canary-secret");

        RegistryIssue issue = Assert.Single(ContractDocuments.ValidateInfraOutputs(document));
        Assert.Equal(RegistryRules.Schema, issue.Rule);
        Assert.DoesNotContain("canary-secret", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Infrastructure_outputs_that_reach_into_the_registry_defs_fail_on_them()
    {
        // The client id rule exists once, in registry.schema.json#/$defs/clientId: a space fails it here
        // only if the two schemas are in one registry.
        string document = InfraOutputs(outputs => outputs["clients"]!["app1"]!["clientId"] = "has space");

        Assert.Contains(ContractDocuments.ValidateInfraOutputs(document), issue => issue.Path == "/clients/app1/clientId");
    }

    [Fact]
    public void Infrastructure_outputs_that_are_not_json_fail_as_not_json()
    {
        Assert.Equal(RegistryRules.NotJson, Assert.Single(ContractDocuments.ValidateInfraOutputs("{\"region\":")).Rule);
    }

    [Fact]
    public void The_example_platform_configuration_passes_and_a_broken_one_fails()
    {
        JsonObject config = Examples.Json("platform-config.example.json");
        Assert.Empty(ContractDocuments.ValidatePlatformConfiguration(config.ToJsonString()));

        config["Policy"]!["AllowedHostSuffixes"] = "not-an-array";
        Assert.NotEmpty(ContractDocuments.ValidatePlatformConfiguration(config.ToJsonString()));
    }

    [Theory]
    [InlineData("app1", true)]
    [InlineData("app-3", true)]
    [InlineData("ab", false)]
    [InlineData("App1", false)]
    [InlineData("../app1", false)]
    [InlineData("app1/releases", false)]
    public void An_app_id_is_checked_against_the_registry_schema(string appId, bool valid)
    {
        Assert.Equal(valid, ContractDocuments.IsAppId(appId));
    }

    [Theory]
    [InlineData("2026-10-10.1", true)]
    [InlineData("v1_final", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("has space", false)]
    public void A_release_is_checked_against_the_registry_schema(string release, bool valid)
    {
        Assert.Equal(valid, ContractDocuments.IsRelease(release));
    }

    [Fact]
    public void A_release_longer_than_the_schema_allows_fails()
    {
        Assert.False(ContractDocuments.IsRelease(new string('a', 65)));
        Assert.True(ContractDocuments.IsRelease(new string('a', 64)));
    }
}
