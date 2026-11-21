using System.Runtime.CompilerServices;

namespace Platform.Abstractions.Tests;

/// <summary>
/// The contract tables this project's guards read, each with a parser of its own. #142 (S5) keeps
/// these here rather than in Gateway.TestHarness, which is workstream 6's and which this project must
/// not reference: the composed suites keep their own reader, and the two spell the tables
/// independently on purpose.
///
/// Each parser reads one section, stops at the next "## " heading, skips the header and rule rows,
/// and refuses an empty table, so that a renamed heading cannot pass as an empty contract.
/// <c>RuleIdsTests</c> is the prior art. A list the contract writes as one prose sentence, the
/// reserved paths and the fragment directives (#1432), is read instead between the sentence's fixed
/// opening and closing, and refuses a missing opening, a missing closing or an empty list for the
/// same reason.
/// </summary>
internal static class ContractTables
{
    /// <summary>The rows of contracts/http-contract.md -> Errors, as (status, code).</summary>
    public static (int Status, string Code)[] Errors() =>
        ParseErrors(File.ReadAllLines(Path.Combine(ContractsDirectory(), "http-contract.md")));

    /// <summary>The lines of contracts/request-gate.md, for a reader of its Rule ids table.</summary>
    public static string[] RequestGate() =>
        File.ReadAllLines(Path.Combine(ContractsDirectory(), "request-gate.md"));

    /// <summary>
    /// The Errors table: the first cell a status, the second the backticked <c>code</c>.
    /// </summary>
    public static (int Status, string Code)[] ParseErrors(IEnumerable<string> lines) =>
    [
        .. Rows(lines, "## Errors", header: "Status")
            .Select(cells => (int.Parse(cells[0], System.Globalization.CultureInfo.InvariantCulture), cells[1].Trim('`'))),
    ];

    /// <summary>
    /// The <c>code</c> column of the Rule ids table, by rule id: the last cell of each row. A row
    /// whose cell is <c>—</c>, an id that ends on no problem body, is left out, as
    /// <see cref="Rejection.Rows"/> leaves it out.
    /// </summary>
    public static (string Rule, string Code)[] ParseRuleCodes(IEnumerable<string> lines)
    {
        string[][] rows = [.. Rows(lines, "## Rule ids", header: "Rule id")];
        Assert.All(rows, cells => Assert.True(cells.Length >= 3, "a Rule ids row has no `code` cell"));

        return
        [
            .. rows
                .Where(cells => cells[^1] != "—")
                .Select(cells => (cells[0].Trim('`'), cells[^1].Trim('`'))),
        ];
    }

    /// <summary>
    /// The directives of contracts/http-contract.md -> "The baseline content security policy", in the
    /// order its text block writes them.
    /// </summary>
    public static string[] BaselineCspDirectives() =>
        ParseBaselineCspDirectives(File.ReadAllLines(Path.Combine(ContractsDirectory(), "http-contract.md")));

    /// <summary>
    /// The paths of contracts/http-contract.md -> Endpoints' "A path reserved outright" sentence, as
    /// it writes them, templated ones and trailing slashes included, in its order.
    /// </summary>
    public static string[] ReservedOutrightPaths() =>
        ParseReservedOutrightPaths(File.ReadAllLines(Path.Combine(ContractsDirectory(), "http-contract.md")));

    /// <summary>
    /// The backticked paths after "A path reserved outright is served from no release, the shell's or a
    /// fragment's:" and before the sentence's full stop, so that the next sentence's
    /// <c>/fragments/</c>, reserved only from the shell's release, is not read.
    /// </summary>
    public static string[] ParseReservedOutrightPaths(IEnumerable<string> lines) =>
        BacktickedBetween(
            lines,
            "A path reserved outright is served from no release, the shell's or a fragment's:",
            ". ");

    /// <summary>
    /// The directives of contracts/http-contract.md's "A fragment's <c>csp</c> adds sources to"
    /// sentence, in its order.
    /// </summary>
    public static string[] FragmentCspDirectives() =>
        ParseFragmentCspDirectives(File.ReadAllLines(Path.Combine(ContractsDirectory(), "http-contract.md")));

    /// <summary>
    /// The backticked directives after "A fragment's `csp` adds sources to" and before "only", so that
    /// the same sentence's later <c>script-src</c> and <c>'strict-dynamic'</c> are not read. The whole
    /// phrase is the anchor: an entry's <c>csp</c> sentence also "adds sources to".
    /// </summary>
    public static string[] ParseFragmentCspDirectives(IEnumerable<string> lines) =>
        BacktickedBetween(lines, "A fragment's `csp` adds sources to", " only");

    /// <summary>
    /// The backticked spans of the prose between <paramref name="opening"/> and the first
    /// <paramref name="closing"/> after it, the lines joined so that a sentence may wrap. A missing
    /// opening or closing fails, and so does a span with no backticked text, so that a reworded
    /// sentence cannot pass as an empty list.
    /// </summary>
    private static string[] BacktickedBetween(IEnumerable<string> lines, string opening, string closing)
    {
        string text = string.Join(' ', lines.Select(line => line.Trim()));

        int start = text.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the contract has no sentence opening \"{opening}\"");
        start += opening.Length;

        int end = text.IndexOf(closing, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"the sentence opening \"{opening}\" has no \"{closing}\"");

        string[] spans = [.. text[start..end].Split('`').Where((_, index) => index % 2 == 1)];
        Assert.NotEmpty(spans);
        return spans;
    }

    /// <summary>
    /// The first fenced block under the "### The baseline content security policy" heading, joined and
    /// split on <c>;</c>, each part's first word. A missing heading or an empty block fails, so a
    /// renamed heading cannot pass as an empty policy.
    /// </summary>
    public static string[] ParseBaselineCspDirectives(IEnumerable<string> lines)
    {
        List<string> block = [];
        bool inSection = false;
        bool inFence = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (!inSection)
            {
                inSection = trimmed == "### The baseline content security policy";
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    break;
                }

                inFence = true;
                continue;
            }

            if (inFence)
            {
                block.Add(trimmed);
            }
            else if (trimmed.StartsWith('#'))
            {
                break;
            }
        }

        string[] directives =
        [
            .. string.Join(' ', block)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split(' ')[0]),
        ];

        Assert.NotEmpty(directives);
        return directives;
    }

    private static IEnumerable<string[]> Rows(IEnumerable<string> lines, string heading, string header)
    {
        List<string[]> rows = [];
        bool inSection = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = line.Trim() == heading;
                continue;
            }

            if (!inSection || !line.StartsWith('|'))
            {
                continue;
            }

            string[] cells = [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];
            if (cells[0] == header || cells[0].StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(cells);
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    /// <summary>
    /// The source tree first, then the output directory's ancestors, the same two places
    /// Contracts.Tests looks. This file sits at tests/Platform.Abstractions.Tests/.
    /// </summary>
    private static string ContractsDirectory([CallerFilePath] string? thisFile = null)
    {
        if (thisFile is not null)
        {
            string fromSource =
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "contracts"));

            if (Directory.Exists(fromSource))
            {
                return fromSource;
            }
        }

        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return Path.Combine(candidate.FullName, "contracts");
            }
        }

        throw new InvalidOperationException("contracts/ is neither beside the source nor above the output.");
    }
}
