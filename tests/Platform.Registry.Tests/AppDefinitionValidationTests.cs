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

    [Fact]
    public void A_timeout_above_the_contract_ceiling_is_out_of_range_in_a_definition_too()
    {
        // The definition reaches registry.schema.json#/$defs/api by $ref, so spactl app add refuses
        // what the gateway would refuse, under the same id and for the same stated reason (ADR-0019).
        long ceiling = Examples.TimeoutSecondsMaximum();

        Assert.True(Validate(Definition(d => d["apis"]![0]!["timeoutSeconds"] = ceiling)).IsValid, "the ceiling itself");

        RegistryIssue error = Assert.Single(Validate(Definition(d => d["apis"]![0]!["timeoutSeconds"] = ceiling + 1)).Errors);
        Assert.Equal((RegistryRules.IntegerOutOfRange, "/apis/0/timeoutSeconds"), (error.Rule, error.Path));
        RegistryValidatorTests.AssertNamesTheCeilingAndWhy(error.Message, ceiling);
    }

    /// <summary>
    /// Schema-valid integers the model cannot hold exactly (RegistryValidatorTests has the registry's
    /// cases). Refused in the definition, so <c>spactl app add</c> never completes one into a registry
    /// entry that the gateway would then refuse.
    /// </summary>
    [Theory]
    [InlineData("/apis/0/timeoutSeconds", "30.0000000000000001")]
    [InlineData("/session/lifetimeMinutes", "480.0000000000000001")]
    public void An_integer_the_model_cannot_hold_is_refused_in_a_definition_too(string path, string spelling)
    {
        AppDefinitionValidation result = Validate(Definition(d =>
        {
            if (path == "/session/lifetimeMinutes")
            {
                d["session"]!["lifetimeMinutes"] = JsonNode.Parse(spelling);
            }
            else
            {
                d["apis"]![0]!["timeoutSeconds"] = JsonNode.Parse(spelling);
            }
        }));

        RegistryIssue error = Assert.Single(result.Errors);
        Assert.Equal((RegistryRules.IntegerOutOfRange, path), (error.Rule, error.Path));
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
