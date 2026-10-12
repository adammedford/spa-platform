using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// "An app definition file is an entry without clientId, clientSecretRef and release." Terraform
/// reads it to create the OAuth client; spactl app add completes it from the infrastructure outputs.
/// </summary>
public sealed class AppDefinitionTests
{
    [Theory]
    [InlineData("clientId", "1example23456789")]
    [InlineData("clientSecretRef", "ssm:/spa-platform/poc/apps/app1/client-secret:1")]
    public void The_three_fields_spactl_completes_cannot_be_named_in_a_definition(string property, string value)
    {
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["oauth"]!.AsObject()[property] = value),
            $"a definition that names {property}");
    }

    [Fact]
    public void Release_cannot_be_named_in_a_definition()
    {
        // Activation is a registry change, so a definition never carries a release.
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["release"] = "2026-10-10.1"),
            "a definition that names a release");
    }

    [Theory]
    [InlineData("appId")]
    [InlineData("host")]
    [InlineData("oauth")]
    [InlineData("session")]
    public void A_definition_needs_everything_terraform_and_spactl_read(string property)
    {
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d.Remove(property)),
            $"a definition with no {property}");
    }

    [Fact]
    public void A_definition_may_leave_out_apis_and_csp()
    {
        Schemas.AcceptsAppDefinition(Documents.AppDefinition(d => d.Remove("apis")), "a definition with no apis");
        Schemas.AcceptsAppDefinition(Documents.AppDefinition(d => d.Remove("csp")), "a definition with no csp");
    }

    [Fact]
    public void A_definition_shares_the_registry_entry_rules()
    {
        // The shared rules are $refs into registry.schema.json, so they cannot drift. One spot check
        // of each kind is enough.
        Schemas.RejectsAppDefinition(Documents.AppDefinition(d => d["appId"] = "App1"), "an uppercase appId");
        Schemas.RejectsAppDefinition(Documents.AppDefinition(d => d["host"] = "App1.spa-poc.test"), "an uppercase host");
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["oauth"]!.AsObject()["scopes"] = Documents.Strings("profile")),
            "scopes without openid");
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["session"] = new JsonObject { ["lifetimeMinutes"] = 721 }),
            "721 minutes");
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["csp"] = new JsonObject { ["script-src"] = Documents.Strings("'unsafe-inline'") }),
            "'unsafe-inline' in script-src");
        Schemas.RejectsAppDefinition(
            Documents.AppDefinition(d => d["apis"] = new JsonArray(
                new JsonObject { ["name"] = "orders", ["upstream"] = "http://127.0.0.1:5080/orders", ["timeoutSeconds"] = 30 })),
            "an upstream with a path");
    }

    [Fact]
    public void A_typo_in_a_definition_is_rejected_rather_than_ignored()
    {
        Schemas.RejectsAppDefinition(Documents.AppDefinition(d => d["hosts"] = "app1.spa-poc.test"), "host misspelled");
    }
}
