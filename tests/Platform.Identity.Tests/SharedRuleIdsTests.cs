using System.Runtime.CompilerServices;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.5a: <c>src/Platform.Identity</c> spells no rule id of its own. The two ids only workstream 3
/// raises, <c>auth.callback</c> and <c>auth.session</c> (contracts/request-gate.md -> Rule ids), are
/// read from <see cref="RuleIds"/>, where ADR-0015 put all twelve because four workstreams raise them.
/// <c>tests/Platform.Abstractions.Tests/RuleIdsTests</c> holds those constants to the contract's
/// table; this holds this project to the constants.
///
/// <para>The same guard stands in <c>tests/Platform.Proxy.Tests</c> and
/// <c>tests/Platform.Security.Tests</c>. It is written again rather than shared because a test project
/// may only write its own paths, and the one project they all reference, <c>Platform.Testing</c>, is
/// the integrator's.</para>
///
/// <para>Test projects keep their literals deliberately, so the scan reads <c>src/</c> only: an
/// assertion that named <see cref="RuleIds"/> would share one symbol with the stage under test, and
/// a wrong-but-consistent id would pass.</para>
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
        // constant the shell gate now reads, and a rule id named in a doc comment.
        string[] lines =
        [
            "/// <c>auth.session</c> (contracts/request-gate.md): the cookie resolved to no session.",
            "context.Features.Get<IRequestTelemetryFeature>()?.Rule = RuleIds.AuthSession;",
            "context.Features.Get<IRequestTelemetryFeature>()?.Rule = \"auth.session\";",
            "private const string Callback = \"auth.callback\";",
        ];

        Assert.Equal(["3: \"auth.session\"", "4: \"auth.callback\""], Copies(lines));
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
    /// <c>tests/Platform.Identity.Tests/</c>.
    /// </summary>
    private static string SourceDirectory([CallerFilePath] string? thisFile = null)
    {
        if (thisFile is not null)
        {
            string fromSource = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "Platform.Identity"));

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
                return Path.Combine(candidate.FullName, "src", "Platform.Identity");
            }
        }

        throw new InvalidOperationException(
            "src/Platform.Identity is neither beside this test project nor above the test output.");
    }
}
