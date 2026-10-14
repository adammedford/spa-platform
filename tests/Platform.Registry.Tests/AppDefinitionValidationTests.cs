using System.Text.Json.Nodes;

namespace Platform.Registry.Tests;

/// <summary>
/// An app definition, checked against <c>app-definition.schema.json</c>. That schema reaches into
/// <c>registry.schema.json#/$defs</c> by <c>$id</c>, so these fail if the schemas are not loaded into
/// one registry (contracts/README.md, "Using the schemas").
/// </summary>
public sealed class AppDefinitionValidationTests
{
    private static JsonObject Definition(Action<JsonObject>? change = null)
    {
        JsonObject document = Examples.Json("app-definition.example.json");
        change?.Invoke(document);
        return document;
    }

    private static AppDefinitionValidation Validate(JsonNode document) =>
        RegistryValidator.ValidateAppDefinition(document.ToJsonString(), Examples.Policy());

    [Fact]
    public void The_contract_example_is_accepted()
    {
        AppDefinitionValidation result = Validate(Definition());
        Assert.True(result.IsValid, string.Join('\n', result.Errors));
    }

    [Fact]
    public void A_rule_reached_through_a_cross_file_ref_is_enforced()
    {
        // appId's pattern lives in registry.schema.json; only the cross-file $ref reaches it.
        AppDefinitionValidation result = Validate(Definition(d => d["appId"] = "App_1"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Rule == RegistryRules.Schema && error.Path == "/appId");
    }

    [Fact]
    public void A_definition_naming_a_client_id_is_rejected()
    {
        AppDefinitionValidation result = Validate(Definition(d => d["oauth"]!["clientId"] = "1example23456789"));
        Assert.Contains(result.Errors, error => error.Rule == RegistryRules.Schema && error.Path == "/oauth/clientId");
    }

    [Fact]
    public void The_policy_rules_apply_to_a_definition()
    {
        Assert.Contains(Validate(Definition(d => d["host"] = "app1.example.test:8443")).Errors,
            error => error.Rule == RegistryRules.HostSuffixNotAllowed && error.Path == "/host");
        Assert.Contains(Validate(Definition(d => d["oauth"]!["issuer"] = "primary")).Errors,
            error => error.Rule == RegistryRules.UnknownIssuer && error.Path == "/oauth/issuer");
        Assert.Contains(Validate(Definition(d => d["apis"]![0]!["upstream"] = "http://127.0.0.1:5082")).Errors,
            error => error.Rule == RegistryRules.UpstreamNotAllowed && error.Path == "/apis/0/upstream");
        Assert.Contains(Validate(Definition(d => d["apis"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "orders", ["upstream"] = "http://127.0.0.1:5081", ["timeoutSeconds"] = 30,
            }))).Errors,
            error => error.Rule == RegistryRules.DuplicateApiName && error.Path == "/apis/1/name");
    }

    [Theory]
    [InlineData("app1.json", 1)]
    [InlineData("app2.json", 1)]
    public void The_fixture_definitions_are_valid_and_two_upstream_origins_warn(string file, int warnings)
    {
        // fixtures/apps: app 1 and app 2 each name both mock API origins, which N-17 relies on.
        AppDefinitionValidation result = RegistryValidator.ValidateAppDefinition(Examples.Text(Path.Combine("apps", file)), Examples.Policy());

        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        Assert.Equal(warnings, result.Warnings.Count(warning => warning.Rule == RegistryRules.MultipleUpstreamOrigins));
    }
}
