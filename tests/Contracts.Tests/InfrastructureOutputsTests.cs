using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// infra/envs/poc/outputs.json. The scripts and the deployed tests read it, and none of them reads
/// Terraform state, so a missing key has to fail here rather than at the point of use.
/// </summary>
public sealed class InfrastructureOutputsTests
{
    [Theory]
    [InlineData("region")]
    [InlineData("cell")]
    [InlineData("bucket")]
    [InlineData("albDnsName")]
    [InlineData("ecsCluster")]
    [InlineData("ecsService")]
    [InlineData("ecrGateway")]
    [InlineData("ecrMockApi")]
    [InlineData("gatewayImage")]
    [InlineData("issuer")]
    [InlineData("cognitoDomain")]
    [InlineData("clients")]
    [InlineData("testUsers")]
    [InlineData("logGroup")]
    public void Every_output_the_contract_lists_is_required(string key)
    {
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o.Remove(key)), $"outputs with no {key}");
    }

    [Fact]
    public void A_typo_is_rejected_rather_than_ignored()
    {
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["ecrGatewayUri"] = "x"), "an output the contract does not list");
    }

    [Fact]
    public void Region_is_an_aws_region()
    {
        Schemas.AcceptsInfraOutputs(Documents.InfraOutputs(o => o["region"] = "eu-west-2"), "another region");
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["region"] = "US-East-1"), "an uppercase region");
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["region"] = "us-east"), "a region with no number");
    }

    [Fact]
    public void Cell_matches_the_platform_configuration_cell()
    {
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["cell"] = "POC"), "an uppercase cell name");
    }

    [Fact]
    public void GatewayImage_is_pinned_by_digest()
    {
        // Deployment pushes by digest and then asserts the running one, so a tag never decides what runs.
        Schemas.AcceptsInfraOutputs(
            Documents.InfraOutputs(o => o["gatewayImage"] =
                "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway@sha256:" + new string('a', 64)),
            "a full-length digest");

        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["gatewayImage"] = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway:latest"),
            "an image pinned by tag");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["gatewayImage"] = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway"),
            "an image with no digest");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["gatewayImage"] = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway@sha256:zzzz1234"),
            "a digest that is not hexadecimal");
    }

    [Fact]
    public void Issuer_is_an_https_authority_and_the_cognito_domain_is_a_host()
    {
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["issuer"] = "cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"),
            "an issuer with no scheme");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["cognitoDomain"] = "https://spa-poc.auth.us-east-1.amazoncognito.com"),
            "a managed-login domain with a scheme, because BrowserOrigin adds it");
    }

    [Fact]
    public void A_client_names_a_parameter_and_a_version_and_never_a_secret()
    {
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["clients"]!["app1"]!.AsObject()["clientSecret"] = "not-a-real-secret"),
            "a client entry carrying a secret value");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["clients"]!["app1"]!.AsObject().Remove("secretParameter")),
            "a client with no secret parameter");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["clients"]!["app1"]!.AsObject()["secretVersion"] = 0),
            "secret version 0");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["clients"]!["app1"]!.AsObject()["secretParameter"] = "spa-platform/poc/apps/app1/client-secret"),
            "a parameter name with no leading slash");
    }

    [Fact]
    public void A_client_is_keyed_by_appId_because_spactl_app_add_looks_it_up_by_one()
    {
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o =>
            {
                var clients = o["clients"]!.AsObject();
                JsonNode client = clients["app1"]!.DeepClone();
                clients.Remove("app1");
                clients["App1"] = client;
            }),
            "a client keyed by something that is not an appId");
    }

    [Fact]
    public void A_test_user_names_a_parameter_and_never_a_password()
    {
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["testUsers"]!["alice"]!.AsObject()["password"] = "not-a-real-password"),
            "a test user carrying a password value");
        Schemas.RejectsInfraOutputs(
            Documents.InfraOutputs(o => o["testUsers"]!["alice"]!.AsObject().Remove("passwordParameter")),
            "a test user with no password parameter");
        Schemas.AcceptsInfraOutputs(Documents.InfraOutputs(o => o["testUsers"] = new JsonObject()), "a pool with no test users");
    }

    [Fact]
    public void LogGroup_is_an_absolute_log_group_name()
    {
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["logGroup"] = "spa-platform/poc/gateway"), "a log group with no leading slash");
        Schemas.RejectsInfraOutputs(Documents.InfraOutputs(o => o["logGroup"] = ""), "an empty log group");
    }

    [Fact]
    public void An_empty_clients_map_is_valid_before_any_app_exists()
    {
        // Task 1.8 creates the clients. The identity root (1.1) applies with none.
        Schemas.AcceptsInfraOutputs(Documents.InfraOutputs(o => o["clients"] = new JsonObject()), "a cell with no app clients yet");
    }
}
