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
/// <c>RuleIdsTests</c> is the prior art.
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
