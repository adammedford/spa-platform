namespace Gateway.ArchitectureTests;

/// <summary>
/// No workstream project spells a rule id as a quoted literal. ADR-0015 put the twelve ids of
/// <c>contracts/request-gate.md</c> in <c>Platform.Abstractions.RuleIds</c> because four workstreams
/// raise them, and a copy nobody checks is how the gateway comes to refuse under an id the contract
/// does not list. <c>RuleIdsTests</c> in <c>tests/Platform.Abstractions.Tests</c> holds those
/// constants to the contract's table; this holds the workstream projects to the constants (#154).
///
/// <para>A rule over directories and ids rather than an assertion about this repository, for the
/// reason <see cref="ReferenceRules"/> gives: <see cref="RuleIdLiteralTests"/> feeds it sources that
/// break it, so the live assertion in <see cref="ArchitectureTests"/> is known to be able to fail.</para>
///
/// <para>It matches the quoted form wherever it appears, comments included, as the per-owner copies
/// of this scan in the Identity, Proxy and Security test projects do. A rule id named in prose is
/// written in a <c>&lt;c&gt;</c> element and is not quoted, so the doc comments that explain a
/// refusal do not trip it. The caller passes source projects only: a test project keeps its literals
/// deliberately, because an assertion that named the constant would share one symbol with the stage
/// under test, and a wrong-but-consistent id would pass.</para>
/// </summary>
public static class RuleIdLiterals
{
    /// <summary>
    /// One <c>path:line: "id"</c> string for each id found quoted on a line of a project's C# sources,
    /// outside the build output under <c>bin/</c> and <c>obj/</c>; empty when the rule holds. The
    /// path starts at the project directory's own name, the one name the rule is given that a reader
    /// can act on, with forward slashes. The order is the projects', then the files' by path, then the
    /// lines', then the ids'.
    /// </summary>
    /// <param name="projects">Each project's directory.</param>
    /// <param name="ids">The rule ids, <c>RuleIds.All</c> for the live assertion.</param>
    /// <exception cref="DirectoryNotFoundException">
    /// A project directory is not there. A scan over it would read no file and answer as a clean
    /// project does, so it is an error rather than an empty answer.
    /// </exception>
    public static IEnumerable<string> Violations(IEnumerable<string> projects, IEnumerable<string> ids)
    {
        string[] quoted = [.. ids.Select(id => $"\"{id}\"")];

        foreach (string project in projects)
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
            string name = Path.GetFileName(directory);

            foreach (string relative in SourceFiles(directory))
            {
                string[] lines = File.ReadAllLines(Path.Combine(directory, relative));

                for (int line = 0; line < lines.Length; line++)
                {
                    foreach (string id in quoted.Where(id => lines[line].Contains(id, StringComparison.Ordinal)))
                    {
                        yield return $"{name}/{relative.Replace('\\', '/')}:{line + 1}: {id}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// A project's <c>*.cs</c> files, relative to its directory and in ordinal order, leaving out the
    /// build's output and its generated copies. The segments are matched below the project directory
    /// only, so a checkout that itself sits under a folder named <c>bin</c> or <c>obj</c> is still read.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file))
            .Where(relative => !relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .SkipLast(1)
                .Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
}
