using System.Runtime.CompilerServices;
using Platform.Abstractions;

namespace Platform.Security.Tests;

/// <summary>
/// Task 4.3b: <c>src/Platform.Security</c> spells no rule id of its own. ADR-0015 put the twelve ids
/// of <c>contracts/request-gate.md</c> in <see cref="RuleIds"/> because four workstreams raise them,
/// and a copy nobody checks is how the gateway comes to refuse under an id the contract does not
/// list: tasks 2.3a and 4.3a each wrote one in good faith on the same day.
///
/// <c>tests/Platform.Abstractions.Tests/RuleIdsTests</c> holds the constants to the contract's own
/// table. This holds this project to the constants, and it reads the source rather than the running
/// code on purpose: a literal that agrees with the contract today is exactly what the next edit
/// breaks, and both ends of a request would still agree while it did.
///
/// <para>The test projects keep their literals deliberately, so this scan reads <c>src/</c> only. An
/// assertion that named <see cref="RuleIds"/> would share one symbol with the stage it tests, so a
/// wrong-but-consistent id would pass; the literal in a test is what proves the stage emitted the
/// string a human wrote.</para>
/// </summary>
public sealed class SharedRuleIdsTests
{
    [Fact]
    public void No_source_file_spells_a_rule_id_as_a_literal()
    {
        List<string> copies = [];
        foreach (string file in SourceFiles())
        {
            copies.AddRange(Copies(File.ReadAllLines(file)).Select(found => $"{Path.GetFileName(file)}:{found}"));
        }

        Assert.Empty(copies);
    }

    [Fact]
    public void The_scan_finds_a_planted_literal_and_passes_the_constant_and_the_prose()
    {
        // The mutation this guard exists to catch, beside the two shapes that must not trip it: the
        // constant the stage now reads, and a rule id named in a doc comment.
        string[] lines =
        [
            "/// <summary>Rule <c>gate.origin</c>: an <c>Origin</c>, when sent, equals the app origin.</summary>",
            "return RefuseAsync(context, StatusCodes.Status403Forbidden, \"origin_rejected\", RuleIds.GateOrigin);",
            "return RefuseAsync(context, StatusCodes.Status403Forbidden, \"origin_rejected\", \"gate.origin\");",
        ];

        Assert.Equal(["3: \"gate.origin\""], Copies(lines));
    }

    /// <summary>Every rule id this file finds spelled as a string literal, with its line number.</summary>
    private static string[] Copies(IReadOnlyList<string> lines)
    {
        List<string> found = [];
        for (int line = 0; line < lines.Count; line++)
        {
            found.AddRange(
                RuleIds.All
                    .Where(id => lines[line].Contains($"\"{id}\"", StringComparison.Ordinal))
                    .Select(id => $"{line + 1}: \"{id}\""));
        }

        return [.. found];
    }

    /// <summary>The project's own sources, without the build's output or its generated copies.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        return Directory.EnumerateFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(obj, StringComparison.Ordinal) && !file.Contains(bin, StringComparison.Ordinal));
    }

    /// <summary>
    /// The project this test project owns. The source tree first, then the output directory's
    /// ancestors, the two places <c>RuleIdsTests</c> looks for the contracts. It throws rather than
    /// passing vacuously on a machine where neither is there. This file sits at
    /// <c>tests/Platform.Security.Tests/</c>.
    /// </summary>
    private static string SourceDirectory([CallerFilePath] string? thisFile = null)
    {
        if (thisFile is not null)
        {
            string fromSource = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "Platform.Security"));

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
                return Path.Combine(candidate.FullName, "src", "Platform.Security");
            }
        }

        throw new InvalidOperationException(
            "src/Platform.Security is neither beside this test project nor above the test output.");
    }
}
