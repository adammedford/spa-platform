using Platform.Abstractions;

namespace Gateway.ArchitectureTests;

/// <summary>
/// No workstream project spells a rule id or a problem code as a quoted literal. ADR-0015 put the twelve ids of
/// <c>contracts/request-gate.md</c> in <c>Platform.Abstractions.RuleIds</c> because four workstreams
/// raise them, and a copy nobody checks is how the gateway comes to refuse under an id the contract
/// does not list. <c>RuleIdsTests</c> in <c>tests/Platform.Abstractions.Tests</c> holds those
/// constants to the contract's table; this holds the workstream projects to the constants (#154).
/// The problem codes of <c>Platform.Abstractions.Problems</c>, the <c>code</c> of a problem body,
/// are held the same way (#158): see <see cref="Ids"/>. They stay two terms, since several rule ids
/// end in one code, but the rule treats them alike, so "id" below means either.
///
/// <para>The rule keeps no exemption list. A homonym, a string spelled like a rule id or a problem
/// code that means something else, is written through a library constant of its own vocabulary, as
/// Identity writes the OAuth error value <c>invalid_request</c> through
/// <c>OidcConstants.TokenErrors.InvalidRequest</c>; where no such constant exists, its owner raises
/// it with workstream 6 as a question rather than adding an exemption.</para>
///
/// <para>A rule over directories and ids rather than an assertion about this repository, for the
/// reason <see cref="ReferenceRules"/> gives: <see cref="RuleIdLiteralTests"/> feeds it sources that
/// break it, so the live assertion in <see cref="ArchitectureTests"/> is known to be able to fail.</para>
///
/// <para>It matches the quoted form wherever it appears, comments included: the behaviour of the
/// per-owner scans in the Identity, Proxy and Security test projects that it replaces. A rule id
/// named in prose is written in a <c>&lt;c&gt;</c> element and is not quoted, so the doc comments
/// that explain a refusal do not trip it. The caller passes source projects only: a test project
/// keeps its literals deliberately, because an assertion that named the constant would share one
/// symbol with the stage under test, and a wrong-but-consistent id would pass.</para>
/// </summary>
public static class RuleIdLiterals
{
    /// <summary>
    /// What the rule holds the workstream projects to: the twelve rule ids of <see cref="RuleIds.All"/>
    /// and the twelve problem codes of <see cref="Problems.Rows"/>, the <c>code</c> column of
    /// <c>contracts/http-contract.md</c> -> Errors (#158). Read from those two types, never listed
    /// here: <c>RuleIdsTests</c> and <c>RejectionTableTests</c> hold the types to the contract tables, so a
    /// row the contract gains reaches this rule without an edit to it. A code is a stable string four
    /// workstreams write, the reason ADR-0015 gave for the rule ids, so a quoted copy of one is the
    /// same fault.
    /// </summary>
    public static IEnumerable<string> Ids => [.. RuleIds.All, .. Problems.Rows.Select(row => row.Code)];

    /// <summary>
    /// One <c>path:line: "id"</c> string for each id found quoted on a line of a project's C# sources,
    /// outside the build output under <c>bin/</c> and <c>obj/</c>; empty when the rule holds. The
    /// path starts at the project directory's own name, the one name the rule is given that a reader
    /// can act on, with forward slashes. The order is the projects', then the files' by path, then the
    /// lines', then the ids'.
    /// </summary>
    /// <param name="projects">Each project's directory.</param>
    /// <param name="ids">The strings to look for, <see cref="Ids"/> for the live assertion.</param>
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
