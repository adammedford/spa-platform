using System.Text.Json.Nodes;

namespace Fixtures.Tests;

/// <summary>
/// <c>fixtures/cognito/pool.json</c>: "two resource servers and two test users". Task 1.1 reads it
/// to create them, and task 5.3 reads the scopes to decide what each mock API listener requires. It
/// holds no password: task 1.1 generates those into Parameter Store, because a credential in the
/// repository is a credential in the repository whatever it protects.
/// </summary>
public sealed class CognitoPoolFixtureTests
{
    [Fact]
    public void There_are_two_resource_servers_one_per_mock_api_listener()
    {
        // Port 5080 is orders and port 5081 is reports (task 5.3). Each listener stands for one API,
        // so each API is one resource server with its own identifier.
        Assert.Equal(
            ["https://orders.spa-poc.test", "https://reports.spa-poc.test"],
            ResourceServers.Select(server => server["identifier"]!.GetValue<string>()));
    }

    [Fact]
    public void Each_resource_server_offers_read_and_admin()
    {
        // F-08 calls /admin on each listener and expects 403. No app asks for the admin scope, so
        // the scope has to exist for the refusal to be about scope rather than about a typo.
        foreach (JsonNode server in ResourceServers)
        {
            Assert.Equal(
                ["read", "admin"],
                server["scopes"]!.AsArray().Select(scope => scope!["name"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void There_are_two_test_users_and_neither_carries_a_password()
    {
        // "Test passwords are generated into Parameter Store" (task 1.1).
        JsonArray users = Pool["users"]!.AsArray();

        Assert.Equal(2, users.Count);
        foreach (JsonNode? user in users)
        {
            Assert.NotEmpty(user!["username"]!.GetValue<string>());
            Assert.NotEmpty(user["email"]!.GetValue<string>());
            Assert.NotEmpty(user["name"]!.GetValue<string>());
            Assert.Null(user["password"]);
        }
    }

    [Fact]
    public void No_two_users_share_a_username_or_an_email()
    {
        JsonArray users = Pool["users"]!.AsArray();

        Assert.Distinct(users.Select(user => user!["username"]!.GetValue<string>()));
        Assert.Distinct(users.Select(user => user!["email"]!.GetValue<string>()));
    }

    [Fact]
    public void Every_scope_an_app_requests_is_one_this_pool_offers()
    {
        // The two files are written by hand and read by two different roots. A scope an app asks for
        // that the pool never creates fails at a login, which is the most expensive place to find it.
        HashSet<string> offered = [.. ResourceServers.SelectMany(server =>
            server["scopes"]!.AsArray().Select(scope =>
                $"{server["identifier"]!.GetValue<string>()}/{scope!["name"]!.GetValue<string>()}"))];

        foreach (string path in FixtureFiles.AppDefinitions.Concat(FixtureFiles.OnboardingDefinitions))
        {
            IEnumerable<string> requested = FixtureFiles.Read(path)["oauth"]!["scopes"]!
                .AsArray()
                .Select(scope => scope!.GetValue<string>())
                // openid and profile are the provider's own, not a resource server's.
                .Where(scope => scope.StartsWith("https://", StringComparison.Ordinal));

            foreach (string scope in requested)
            {
                Assert.Contains(scope, offered);
            }
        }
    }

    [Fact]
    public void Each_apps_resource_binding_names_a_resource_server()
    {
        // The provider sets aud to the resource server's identifier, so a resource that names no
        // resource server would bind the token to an audience the API never checks for.
        HashSet<string> identifiers = [.. ResourceServers.Select(server => server["identifier"]!.GetValue<string>())];

        foreach (string path in FixtureFiles.AppDefinitions.Concat(FixtureFiles.OnboardingDefinitions))
        {
            if (FixtureFiles.Read(path)["oauth"]!["resource"] is { } resource)
            {
                Assert.Contains(resource.GetValue<string>(), identifiers);
            }
        }
    }

    private static JsonNode Pool => FixtureFiles.Read(FixtureFiles.Path("fixtures", "cognito", "pool.json"));

    private static IEnumerable<JsonNode> ResourceServers =>
        Pool["resourceServers"]!.AsArray().Select(server => server!);
}
