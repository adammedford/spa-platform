using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// F-07's plan read (#212): the changes a saved plan of the apps root makes, from <c>terraform show
/// -json</c>, and the per-app resources <c>main.tf</c> declares, against the root itself.
/// </summary>
public sealed class TerraformPlanTests
{
    /// <summary>A value a plan of an applied apps root carries in cleartext (infra/envs/poc/apps/README.md).</summary>
    private const string Canary = "client-secret-canary-0b1c";

    [Fact]
    public void The_per_app_resources_are_every_resource_block_of_the_apps_root_keyed_by_the_app()
    {
        string root = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps", "main.tf"));

        Assert.Equal(
            ["aws_cognito_managed_login_branding.app[\"app3\"]", "aws_cognito_user_pool_client.app[\"app3\"]", "aws_ssm_parameter.client_secret[\"app3\"]"],
            TerraformPlan.PerAppAddresses(root, "app3"));
    }

    [Fact]
    public void A_resource_block_not_keyed_by_app_is_refused_rather_than_left_out()
    {
        const string root = """
            resource "aws_ssm_parameter" "client_secret" {
              for_each = local.apps
            }

            resource "aws_s3_object" "shared" {
              key = "x"
            }
            """;

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => TerraformPlan.PerAppAddresses(root, "app3"));
        Assert.Contains("aws_s3_object.shared", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changes_are_the_resource_changes_that_do_something_and_carry_no_value()
    {
        string plan = Plan(
            ("aws_cognito_user_pool_client.app[\"app1\"]", "managed", ["no-op"]),
            ("aws_cognito_user_pool_client.app[\"app3\"]", "managed", ["create"]),
            ("data.terraform_remote_state.idp", "data", ["read"]),
            ("aws_ssm_parameter.client_secret[\"app1\"]", "managed", ["delete", "create"]));

        IReadOnlyList<PlannedChange> changes = TerraformPlan.Changes(plan);

        Assert.Equal(
            [new PlannedChange("aws_cognito_user_pool_client.app[\"app3\"]", "create"), new PlannedChange("aws_ssm_parameter.client_secret[\"app1\"]", "delete,create")],
            changes);
        Assert.DoesNotContain(Canary, string.Join(' ', changes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_with_no_resource_changes_has_none()
    {
        Assert.Empty(TerraformPlan.Changes("""{"format_version":"1.2"}"""));
    }

    [Fact]
    public void A_document_that_is_not_a_plan_is_refused_without_echoing_it()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => TerraformPlan.Changes("{\"value\":\"" + Canary));
        Assert.DoesNotContain(Canary, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A <c>terraform show -json</c> document of these changes, each carrying the canary as a value, as a real one would.</summary>
    internal static string Plan(params (string Address, string Mode, string[] Actions)[] changes) =>
        new JsonObject
        {
            ["format_version"] = "1.2",
            ["prior_state"] = new JsonObject { ["values"] = new JsonObject { ["secret"] = Canary } },
            ["resource_changes"] = new JsonArray([.. changes.Select(change => (JsonNode)new JsonObject
            {
                ["address"] = change.Address,
                ["mode"] = change.Mode,
                ["change"] = new JsonObject
                {
                    ["actions"] = new JsonArray([.. change.Actions.Select(action => (JsonNode)action)]),
                    ["after"] = new JsonObject { ["value"] = Canary },
                },
            })]),
        }.ToJsonString();
}
