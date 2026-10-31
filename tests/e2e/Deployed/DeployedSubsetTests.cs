using System.Reflection;
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
        string page = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "docs", "plan", "04-tests.md"));
        Assert.Contains("N-26", DeployedRows(page)); // The row only a real browser can decide (R-D3), and the reason for the tier.

        Dictionary<string, string> sources = Directory.GetFiles(NegativeDirectory, "*.cs").ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
        AssertEveryDRowHasATest(page, File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "test-ids.md")), sources);
        Assert.Empty(LocalOnly(sources));
    }

    /// <summary>
    /// Fails, naming each id, when a D row of the export's negative table or of the register's (#322) has
    /// no <c>Task N</c><i>nn</i><c>_</c> test in the sources or explicitly named public continuity Fact (#657).
    /// </summary>
    internal static void AssertEveryDRowHasATest(string page, string register, Dictionary<string, string> sources)
    {
        string[] rows = DeployedRowIds(page, register);
        Dictionary<string, MethodInfo> continuity = ContinuityRowTests(register);
        string[] untested = [.. rows.Where(row => !continuity.ContainsKey(row) && !sources.Values.Any(source => source.Contains($"Task N{row[2..]}_", StringComparison.Ordinal)))];
        Assert.Empty(untested);
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
    public void The_registers_d_rows_are_read_from_its_tier_column_not_the_last_one()
    {
        // contracts/test-ids.md (#322) adds Recorded by after Tier, so Tier is not the row's last cell there.
        const string register = """
            ## Functional (F)

            | ID | Test | Expect | Tier | Criterion | Recorded by |
            | --- | --- | --- | --- | --- | --- |
            | F-12 | a | b | D | c | #322 |

            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            | N-28 | Plant a D cookie | Refused | D | #322 Q8 |
            | N-29 | Forge one | 401 | H | D |
            | N-100 | Replay it | Rejected | H, D | #324 Q22 |

            ## Infrastructure and architecture properties (P)

            | N-99 | not in the table | x | D | #322 |
            """;

        Assert.Equal(["N-28", "N-100"], RegisterDeployedRows(register));
    }

    /// <summary>An export page whose one D row, N-01, the sources below cover.</summary>
    private const string ExportPage = """
        ## Negative security tests

        | ID | Attempt | Expect | Tier |
        | --- | --- | --- | --- |
        | N-01 | Present app 1's session cookie to app 2 | 401 | H, D |
        """;

    [Theory]
    [InlineData("None yet.")]
    [InlineData("| N-28 | Forge one | 401 | H | #322 Q8 |")]
    public void A_register_with_no_n_rows_or_none_marked_d_adds_no_row_and_the_subset_passes(string body)
    {
        string register = $"""
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            {body}

            ## Infrastructure and architecture properties (P)
            """;
        Dictionary<string, string> sources = new() { ["CookieTests.cs"] = "public async Task N01_a_cookie_of_app_1_is_refused_on_app_2()" };

        Assert.Empty(RegisterDeployedRows(register));
        AssertEveryDRowHasATest(ExportPage, register, sources);
    }

    [Fact]
    public void A_register_d_row_with_no_test_fails_the_subset_naming_its_id()
    {
        const string register = """
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            | N-28 | Plant a D cookie | Refused | H, D | #322 Q8 |
            """;
        Dictionary<string, string> sources = new() { ["CookieTests.cs"] = "public async Task N01_a_cookie_of_app_1_is_refused_on_app_2()" };

        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEveryDRowHasATest(ExportPage, register, sources));
        Assert.Contains("N-28", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("N-01", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("| N-28 | Plant a D cookie | Refused | D |")] // Recorded by left out: Expect would sit under Tier.
    [InlineData("| N-28 | Plant | D | #322 Q8 |")] // A cell short: D would be counted by accident.
    public void A_register_row_short_of_its_headers_cells_fails_naming_its_id(string row)
    {
        string register = $"""
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            {row}
            """;

        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => RegisterDeployedRows(register));
        Assert.Contains("N-28", failure.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// The D rows of the export's negative table and of the register's (#322): the one list of deployed
    /// rows this fact and <see cref="DeployedRunTests"/> both hold to a test.
    /// </summary>
    internal static string[] DeployedRowIds(string page, string register) => [.. DeployedRows(page), .. RegisterDeployedRows(register)];

    /// <summary>The ids of the rows in the negative table whose Tier cell names D.</summary>
    internal static string[] DeployedRows(string page)
    {
        int start = page.IndexOf("## Negative security tests", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/plan/04-tests.md has no negative table.");
        int end = page.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string table = end < 0 ? page[start..] : page[start..end];
        return [.. Row().Matches(table)
            .Where(match => match.Groups["tier"].Value.Split(',').Select(tier => tier.Trim()).Contains("D"))
            .Select(match => match.Groups["id"].Value)];
    }

    /// <summary>
    /// The ids of the rows in <c>contracts/test-ids.md</c>'s negative table whose Tier cell names D. The
    /// register's Tier is found by its header, since Recorded by follows it, and counted from the row's
    /// end so a pipe inside an earlier cell cannot move it.
    /// </summary>
    internal static string[] RegisterDeployedRows(string register)
    {
        int start = register.IndexOf("## Negative security (N)", StringComparison.Ordinal);
        Assert.True(start >= 0, "contracts/test-ids.md has no negative table.");
        int end = register.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string[] lines = (end < 0 ? register[start..] : register[start..end]).Split('\n').Select(line => line.Trim()).ToArray();

        string? headerLine = lines.FirstOrDefault(line => line.StartsWith("| ID |", StringComparison.Ordinal));
        Assert.True(headerLine is not null, "contracts/test-ids.md's negative table has no header row.");
        string[] header = Cells(headerLine);
        int fromEnd = header.Length - Array.IndexOf(header, "Tier");
        Assert.True(fromEnd <= header.Length, "contracts/test-ids.md's negative table has no Tier column.");

        string[][] rows = [.. lines.Where(line => RegisterId().IsMatch(line)).Select(Cells)];
        // A row short of the header's cells would put another column under Tier from the end, and a D row
        // missing its Recorded by would drop out unread: fail closed and name it (CODING_STANDARDS.md).
        string[] shortRows = [.. rows.Where(cells => cells.Length < header.Length).Select(cells => cells[0])];
        Assert.True(shortRows.Length == 0, $"contracts/test-ids.md's negative table has rows short of its header's cells: {string.Join(", ", shortRows)}.");

        return [.. rows
            .Where(cells => cells[^fromEnd].Split(',').Select(tier => tier.Trim()).Contains("D"))
            .Select(cells => cells[0])];

        static string[] Cells(string line) => [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];
    }

    /// <summary>
    /// A registered D row may name an existing continuity Fact instead of an N-prefixed test (#657).
    /// Resolve that explicit reference to the real public method; prose naming a missing method proves nothing.
    /// </summary>
    internal static Dictionary<string, MethodInfo> ContinuityRowTests(string register)
    {
        HashSet<string> rows = new(RegisterDeployedRows(register), StringComparer.Ordinal);
        Dictionary<string, MethodInfo> tests = new(StringComparer.Ordinal);
        foreach (string line in register.Split('\n'))
        {
            string id = line.Trim().Trim('|').Split('|')[0].Trim();
            if (!rows.Contains(id) || !line.Contains("`tests/e2e/Deployed/ContinuityTests.cs`", StringComparison.Ordinal))
            {
                continue;
            }

            Match reference = ContinuityReference().Match(line);
            if (!reference.Success)
            {
                continue;
            }

            MethodInfo? method = typeof(ContinuityTests).GetMethod(reference.Groups["method"].Value,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method?.GetCustomAttribute<FactAttribute>() is not null &&
                method.GetCustomAttribute<TheoryAttribute>() is null &&
                method.ReturnType == typeof(Task) && method.GetParameters().Length == 0)
            {
                tests.Add(id, method);
            }
        }

        return tests;
    }

    [GeneratedRegex(@"`ContinuityTests\.(?<method>[A-Za-z_][A-Za-z0-9_]*)`")]
    private static partial Regex ContinuityReference();

    /// <summary>The files that start a stack of their own choosing rather than the one the switch chooses.</summary>
    internal static string[] LocalOnly(Dictionary<string, string> sources) =>
        [.. sources.Where(source => source.Value.Contains("LocalStack.StartAsync(", StringComparison.Ordinal)).Select(source => source.Key).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"^\| (?<id>N-\d\d) \|.*\| (?<tier>[^|]+) \|\s*$", RegexOptions.Multiline)]
    private static partial Regex Row();

    [GeneratedRegex(@"^\| N-\d+ \|")]
    private static partial Regex RegisterId();
}
