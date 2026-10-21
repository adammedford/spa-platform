using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>The deployed stack's fixed values, each held to the file it copies (CODING_STANDARDS.md: a guard reads the thing it guards).</summary>
public sealed partial class DeployedStackTests
{
    [Fact]
    public void The_wait_past_expiry_is_the_access_token_life_the_apps_root_gives_every_client()
    {
        string root = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps", "main.tf"));

        Match validity = AccessTokenValidity().Match(root);
        Match units = AccessTokenUnits().Match(root);
        Assert.True(validity.Success && units.Success, "infra/envs/poc/apps/main.tf no longer sets access_token_validity and its unit.");

        TimeSpan life = units.Groups[1].Value switch
        {
            "seconds" => TimeSpan.FromSeconds(int.Parse(validity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
            "minutes" => TimeSpan.FromMinutes(int.Parse(validity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
            "hours" => TimeSpan.FromHours(int.Parse(validity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
            string other => throw new InvalidOperationException($"unit {other}"),
        };
        Assert.Equal(life, DeployedStack.AccessTokenLifetime);
    }

    [Fact]
    public void The_test_user_is_one_the_identity_root_creates()
    {
        System.Text.Json.Nodes.JsonNode pool = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "cognito", "pool.json")))!;

        Assert.Contains(DeployedStack.User, pool["users"]!.AsArray().Select(user => user!["username"]!.GetValue<string>()));
    }

    [GeneratedRegex(@"^\s*access_token_validity\s*=\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex AccessTokenValidity();

    [GeneratedRegex(@"token_validity_units\s*\{[^}]*?access_token\s*=\s*""(\w+)""", RegexOptions.Singleline)]
    private static partial Regex AccessTokenUnits();
}
