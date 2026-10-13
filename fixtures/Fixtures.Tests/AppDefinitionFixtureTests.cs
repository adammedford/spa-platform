using System.Text.Json.Nodes;

namespace Fixtures.Tests;

/// <summary>
/// Task 5.5's "done when": each definition validates against the registry schema with the client id,
/// the secret reference and the release left out. That schema is
/// <c>contracts/app-definition.schema.json</c>, and leaving those three out is not an omission but
/// the point: Terraform creates the client, and <c>spactl app add</c> completes the entry from the
/// infrastructure outputs, so each app is described once.
/// </summary>
public sealed class AppDefinitionFixtureTests
{
    [Fact]
    public void Apps_one_and_two_are_the_definitions_the_apps_root_creates()
    {
        Assert.Equal(["app1.json", "app2.json"], FixtureFiles.AppDefinitions.Select(Path.GetFileName));
    }

    [Fact]
    public void App_three_waits_under_onboarding()
    {
        // F-07 onboards it while apps 1 and 2 are in use, so it is not in the apps root's directory.
        Assert.Equal(["app3.json"], FixtureFiles.OnboardingDefinitions.Select(Path.GetFileName));
    }

    [Theory]
    [MemberData(nameof(EveryDefinition))]
    public void Every_definition_validates_against_the_app_definition_schema(string path)
    {
        JsonNode definition = FixtureFiles.Read(path);

        (bool isValid, string report) = FixtureFiles.ValidatesAsAppDefinition(definition);

        Assert.True(isValid, $"{Path.GetFileName(path)} should validate, but reported:\n{report}");
    }

    [Fact]
    public void Each_app_lists_the_other_apps_api_as_a_second_upstream()
    {
        // "Each app also lists the other's API as a second upstream, so N-17 has something to
        // refuse": app 1 sends its token to reports and app 2 sends its to orders, and the mock API
        // refuses both. Without the second upstream the gateway would answer 404 unknown_api and
        // N-17 would never reach a listener.
        //
        // Both entries therefore name two upstream origins, which the validator of task 2.1 raises
        // as a warning: Phase 1 sends one access token to each, and that is safe only inside one
        // trust domain. The warning is correct and these fixtures expect it. It is a warning and not
        // an error for exactly this case.
        Assert.Equal(["orders", "reports"], ApiNamesOf("app1"));
        Assert.Equal(["reports", "orders"], ApiNamesOf("app2"));
    }

    [Fact]
    public void App_three_lists_the_reports_api_alone()
    {
        // F-07 onboards app 3 and the mock API refuses its call, because it has not admitted that
        // client. One upstream is all that proof needs.
        Assert.Equal(["reports"], ApiNamesOf("app3"));
    }

    [Fact]
    public void Only_app_one_binds_a_resource()
    {
        // Port 5080 is orders and checks aud, because app 1 uses resource binding. Port 5081 is
        // reports and does not, because app 2 has none (task 5.3).
        Assert.Equal("https://orders.spa-poc.test", OAuthOf("app1")["resource"]?.GetValue<string>());
        Assert.Null(OAuthOf("app2")["resource"]);
        Assert.Null(OAuthOf("app3")["resource"]);
    }

    [Fact]
    public void App_two_is_the_one_app_that_adds_a_content_security_policy_source()
    {
        // Task 5.1: "App 2 needs one extra CSP source, to prove per-app policy." One app with an
        // addition and one without is what makes the policy visibly per-app.
        Assert.Null(FixtureFiles.Definition("app1")["csp"]);

        JsonNode csp = FixtureFiles.Definition("app2")["csp"]!;
        KeyValuePair<string, JsonNode?> addition = Assert.Single(csp.AsObject());
        Assert.Equal("img-src", addition.Key);
        Assert.Equal(["data:"], addition.Value!.AsArray().Select(source => source!.GetValue<string>()));
    }

    [Fact]
    public void No_two_definitions_share_an_app_id_or_a_host()
    {
        // Both are unique across the registry, and the validator of task 2.1 enforces it on the
        // assembled document. Three files assembled into one registry must not break it on arrival.
        List<JsonNode> definitions = [.. FixtureFiles.AllDefinitions.Select(FixtureFiles.Read)];

        Assert.Distinct(definitions.Select(d => d["appId"]!.GetValue<string>()));
        Assert.Distinct(definitions.Select(d => d["host"]!.GetValue<string>()));
    }

    private static IEnumerable<string> ApiNamesOf(string appId) =>
        FixtureFiles.Definition(appId)["apis"]!.AsArray().Select(api => api!["name"]!.GetValue<string>());

    private static JsonObject OAuthOf(string appId) => FixtureFiles.Definition(appId)["oauth"]!.AsObject();

    public static TheoryData<string> EveryDefinition
    {
        get
        {
            TheoryData<string> paths = [];
            foreach (string path in FixtureFiles.AllDefinitions)
            {
                paths.Add(path);
            }

            return paths;
        }
    }
}
