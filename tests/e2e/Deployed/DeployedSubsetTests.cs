using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed negative subset (#211): every row the Tests tab marks D runs on the stack
/// <see cref="StackSwitch"/> chooses, as the functional rows do, so <c>SPA_E2E_STACK=deployed</c> runs
/// the whole subset on the cell and nothing in it quietly stays on the local stack.
/// </summary>
public sealed partial class DeployedSubsetTests
{
    private static readonly string NegativeDirectory = Path.Combine(FixtureFiles.RepositoryRoot, "tests", "e2e", "Negative");

    [Fact]
    public void Every_negative_row_the_tests_tab_marks_d_has_a_test_and_every_negative_test_starts_the_chosen_stack()
    {
        string[] rows = DeployedRows(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "docs", "plan", "04-tests.md")));
        Assert.Contains("N-26", rows); // The row only a real browser can decide (R-D3), and the reason for the tier.

        Dictionary<string, string> sources = Directory.GetFiles(NegativeDirectory, "*.cs").ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
        string[] untested = [.. rows.Where(row => !sources.Values.Any(source => source.Contains($"Task N{row[2..]}_", StringComparison.Ordinal)))];
        Assert.Empty(untested);
        Assert.Empty(LocalOnly(sources));
    }

    [Fact]
    public void Every_functional_test_starts_the_chosen_stack_f07_included()
    {
        // The functional rows of G3 (#209) and F-07 at G5 (#212): SPA_E2E_STACK=deployed runs them all on the cell.
        string[] files = Directory.GetFiles(Path.Combine(FixtureFiles.RepositoryRoot, "tests", "e2e", "Functional"), "*.cs");
        Assert.Contains(files, path => Path.GetFileName(path) == "OnboardingTests.cs");

        Assert.Empty(LocalOnly(files.ToDictionary(path => Path.GetFileName(path), File.ReadAllText)));
    }

    [Fact]
    public void A_negative_test_that_starts_the_local_stack_itself_is_found()
    {
        // The failing input for the rule above: a file as the suite was before #211.
        Dictionary<string, string> sources = new()
        {
            ["Chosen.cs"] = "await using IBrowserStack stack = await Stacks.StartAsync();",
            ["Pinned.cs"] = "await using LocalStack stack = await LocalStack.StartAsync();",
        };

        Assert.Equal(["Pinned.cs"], LocalOnly(sources));
    }

    [Fact]
    public void The_d_rows_are_read_from_the_tier_column_of_the_negative_table_only()
    {
        const string page = """
            ## Functional tests

            | ID | Attempt | Expect | Tier |
            | --- | --- | --- | --- |
            | F-01 | a | b | H, D |

            ## Negative security tests

            | ID | Attempt | Expect | Tier |
            | --- | --- | --- | --- |
            | N-01 | Present a D cookie | 401 | H, D |
            | N-02 | Forge one in D | 401 | H |
            | N-26 | Login CSRF | Rejected | D |

            ## Infrastructure and architecture tests

            | N-99 | not in the table | x | D |
            """;

        Assert.Equal(["N-01", "N-26"], DeployedRows(page));
    }

    [Fact]
    public async Task A_second_assertion_of_the_same_rule_needs_a_line_of_its_own()
    {
        // Rows that refuse twice with one rule (N-06 four times, N-07 three) must see each refusal,
        // not the first one's line again.
        await using LocalStack stack = await LocalStack.StartAsync();
        LogMark mark = stack.MarkLog();
        using HttpRequestMessage unknown = new(HttpMethod.Get, $"https://unknown.{stack.ParentDomain}:8443/");
        using HttpResponseMessage answer = await stack.SendRawAsync(unknown);

        await stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => stack.AssertRuleLoggedAsync(mark, RuleIds.HostUnknown));
    }

    /// <summary>The ids of the rows in the negative table whose Tier cell names D.</summary>
    private static string[] DeployedRows(string page)
    {
        int start = page.IndexOf("## Negative security tests", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/plan/04-tests.md has no negative table.");
        int end = page.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string table = end < 0 ? page[start..] : page[start..end];
        return [.. Row().Matches(table)
            .Where(match => match.Groups["tier"].Value.Split(',').Select(tier => tier.Trim()).Contains("D"))
            .Select(match => match.Groups["id"].Value)];
    }

    /// <summary>The files that start a stack of their own choosing rather than the one the switch chooses.</summary>
    internal static string[] LocalOnly(Dictionary<string, string> sources) =>
        [.. sources.Where(source => source.Value.Contains("LocalStack.StartAsync(", StringComparison.Ordinal)).Select(source => source.Key).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"^\| (?<id>N-\d\d) \|.*\| (?<tier>[^|]+) \|\s*$", RegexOptions.Multiline)]
    private static partial Regex Row();
}
