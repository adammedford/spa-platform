using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// What the deployed stack reads from the infrastructure outputs document and the app definitions
/// (#209): every name of the cell comes from there, never from Terraform state
/// (contracts/infra-outputs.schema.json). Each test reads the contract's own example document, so a
/// change to the contract's shape breaks these rather than a sitting.
/// </summary>
public sealed class CellTests
{
    private static string Example => File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json"));

    [Fact]
    public void An_app_is_its_definitions_host_with_the_client_the_outputs_name()
    {
        Cell cell = Cell.Parse(Example);
        string host = FixtureFiles.AppDefinition("app1")["host"]!.GetValue<string>();

        HarnessApp app = cell.App("app1");

        Assert.Equal("app1", app.AppId);
        Assert.Equal(host, app.Host);
        Assert.Equal(new Uri("https://" + host + "/"), app.Origin);
        Assert.Equal("1example23456789", app.ClientId);
        Assert.Equal("", app.ClientSecret);
    }

    [Fact]
    public void An_app_the_outputs_have_no_client_for_is_refused_by_name()
    {
        Cell cell = Cell.Parse(Example);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => cell.App("app2"));
        Assert.Contains("app2", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_domain_is_the_parent_of_the_app_hosts_and_every_name_under_it_resolves_to_the_tunnel()
    {
        Cell cell = Cell.Parse(Example);

        Assert.Equal("spa-poc.test", cell.Domain);
        Assert.Equal("MAP *.spa-poc.test 127.0.0.1:9443", cell.ResolverRule(9443));
    }

    [Fact]
    public void The_provider_is_the_managed_login_domain_with_cognitos_endpoints()
    {
        Cell cell = Cell.Parse(Example);

        // https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html,
        // .../logout-endpoint.html and .../cognito-user-pools-managed-login.html
        Assert.Equal("spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com", cell.ProviderHost);
        Assert.Equal("https://spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com/oauth2/authorize", cell.AuthorizeEndpoint);
        Assert.Equal("https://spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com/logout", cell.EndSessionEndpoint);
    }

    [Fact]
    public void The_log_group_region_bucket_and_a_test_users_password_parameter_come_from_the_outputs()
    {
        Cell cell = Cell.Parse(Example);

        Assert.Equal("/spa-platform/poc/gateway", cell.LogGroup);
        Assert.Equal("us-east-1", cell.Region);
        Assert.Equal("spa-platform-poc-111122223333-us-east-1-an", cell.Bucket);
        Assert.Equal("/spa-platform/poc/test-users/alice", cell.PasswordParameter("alice"));
        Assert.Contains("carol", Assert.Throws<InvalidOperationException>(() => cell.PasswordParameter("carol")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_missing_a_field_the_stack_reads_is_refused_by_the_fields_name()
    {
        string withoutLogGroup = Example.Replace("\"logGroup\"", "\"logGroupX\"", StringComparison.Ordinal);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => Cell.Parse(withoutLogGroup));
        Assert.Contains("logGroup", refused.Message, StringComparison.Ordinal);
    }
}
