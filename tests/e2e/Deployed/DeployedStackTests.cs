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
    public void Both_test_users_are_ones_the_identity_root_creates_and_they_differ()
    {
        System.Text.Json.Nodes.JsonNode pool = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "cognito", "pool.json")))!;
        string[] users = [.. pool["users"]!.AsArray().Select(user => user!["username"]!.GetValue<string>())];

        Assert.Contains(DeployedStack.User, users);
        Assert.Contains(DeployedStack.OtherUser, users); // N-04, N-06 and N-26 sign the second person in as this one (#211).
        Assert.NotEqual(DeployedStack.User, DeployedStack.OtherUser);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 12)]
    [InlineData(5, 7)]
    [InlineData(12, 0)]
    [InlineData(60, 0)]
    [InlineData(-30, 12)]
    public void A_stack_on_the_cell_waits_out_what_is_left_of_the_quiet_since_the_last_one_ended(int? endedSecondsAgo, int waitSeconds)
    {
        // A mark reaches back of the workstation's clock, so a stack that started sooner could match a
        // line the last test caused and never asserted (N-07's preflight before N-07's fact). An end in
        // the future, a clock set back during the run, still waits one quiet and no more.
        DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        TimeSpan quiet = TimeSpan.FromSeconds(12);
        DateTimeOffset? ended = endedSecondsAgo is { } ago ? now - TimeSpan.FromSeconds(ago) : null;

        Assert.Equal(TimeSpan.FromSeconds(waitSeconds), DeployedStack.StartDelay(now, ended, quiet));
    }

    [Fact]
    public void The_quiet_outlasts_a_mark_reaching_back_from_a_clock_behind_awss_by_the_margin()
    {
        // The last stack's line can carry an AWS time just after it ended, and the new mark reaches the
        // margin back of a clock that may be the margin behind AWS's: twice the margin, and then some.
        Assert.True(DeployedStack.Quiet > 2 * DeployedStack.ClockMargin, $"{DeployedStack.Quiet} is not more than twice {DeployedStack.ClockMargin}.");
    }

    [GeneratedRegex(@"^\s*access_token_validity\s*=\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex AccessTokenValidity();

    [GeneratedRegex(@"token_validity_units\s*\{[^}]*?access_token\s*=\s*""(\w+)""", RegexOptions.Singleline)]
    private static partial Regex AccessTokenUnits();
}
