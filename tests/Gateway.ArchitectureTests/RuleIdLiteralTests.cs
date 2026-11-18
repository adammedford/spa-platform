using Platform.Abstractions;

namespace Gateway.ArchitectureTests;

/// <summary>
/// <see cref="RuleIdLiterals"/>, fed planted sources. Without these the live assertion in
/// <see cref="ArchitectureTests"/> would pass against a rule that can never fail, as
/// <see cref="ReferenceRuleTests"/> says of the reference rules. The lines are the three shapes the
/// per-owner copies of this scan proved: the literal the rule exists to catch, and the constant and
/// the doc-comment mention that must not trip it.
/// </summary>
public sealed class RuleIdLiteralTests : IDisposable
{
    private readonly string project = Directory.CreateTempSubdirectory("rule-id-literals-").FullName;

    private static readonly string[] Planted =
    [
        "/// <summary>The rule id for this refusal, <c>api.unknown</c>.</summary>",
        "private const string Rule = RuleIds.ApiUnknown;",
        "private const string Rule = \"api.unknown\";",
    ];

    [Fact]
    public void A_quoted_id_is_one_violation_at_its_line_and_the_constant_and_the_prose_are_none()
    {
        Write("UnknownApi.cs", Planted);

        Assert.Equal(
            [$"{Path.GetFileName(project)}/UnknownApi.cs:3: \"api.unknown\""],
            RuleIdLiterals.Violations([project], RuleIds.All));
    }

    [Fact]
    public void A_quoted_problem_code_is_one_violation_at_its_line_and_the_constant_and_the_prose_are_none()
    {
        // The same three shapes for a code of contracts/http-contract.md -> Errors (#158): the rule
        // takes the codes as well as the rule ids.
        Write(
            "Answers.cs",
            [
                "/// <summary>A 404, <c>not_found</c>, for an asset that is not there.</summary>",
                "return Problems.WriteAsync(context, Problems.NotFound);",
                "return Problems.WriteAsync(context, \"not_found\");",
            ]);

        Assert.Equal(
            [$"{Path.GetFileName(project)}/Answers.cs:3: \"not_found\""],
            RuleIdLiterals.Violations([project], RuleIdLiterals.Ids));
    }

    [Fact]
    public void The_ids_are_the_fourteen_rule_ids_and_the_fifteen_problem_codes()
    {
        // Counted from the two contract tables, contracts/request-gate.md -> Rule ids and
        // contracts/http-contract.md -> Errors, not from the types the rule reads: RuleIdsTests and
        // RejectionTableTests hold those types to the tables, and this holds the rule to both types.
        // The codes are numbered by landing. The twelfth code is shell_unavailable (#427); the
        // thirteenth code is session_store_unavailable and the thirteenth rule id auth.store, both
        // #343's item 2; the fourteenth code is store_unavailable (#543), which the Errors table and
        // Problems.Rows place after idp_unavailable, not last. The fourteenth rule id is auth.chain
        // and the fifteenth code fragment_login_required, both the fragments spec's (#961), typed by
        // #988, with the Errors row of #982 and the Rule ids row of #983; the Errors table and
        // Problems.Rows place fragment_login_required after session_ended, not last. Mutation (#1002):
        // remove only the AuthChain entry from RuleIds.All and the FragmentLoginRequired entry from
        // Problems.Rows, keeping the constants, and the count reads 27 and neither auth.chain nor
        // fragment_login_required is in the set. Rejection names RuleIds.AuthChain, so dropping the
        // constant does not compile.
        Assert.Equal(29, RuleIdLiterals.Ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("host.unknown", RuleIdLiterals.Ids);
        Assert.Contains("api.unknown", RuleIdLiterals.Ids);
        Assert.Contains("invalid_request", RuleIdLiterals.Ids);
        Assert.Contains("upstream_timeout", RuleIdLiterals.Ids);
        Assert.Contains("shell_unavailable", RuleIdLiterals.Ids);
        Assert.Contains("session_store_unavailable", RuleIdLiterals.Ids);
        Assert.Contains("store_unavailable", RuleIdLiterals.Ids);
        Assert.Contains("auth.store", RuleIdLiterals.Ids);
        Assert.Contains("auth.chain", RuleIdLiterals.Ids);
        Assert.Contains("fragment_login_required", RuleIdLiterals.Ids);
    }

    [Fact]
    public void A_quoted_id_in_the_build_output_or_outside_a_cs_file_is_none()
    {
        // The build writes generated copies under obj/ and bin/, and a project carries other files
        // (a .csproj comment, a README) the compiler never reads.
        Write(Path.Combine("obj", "Debug", "Generated.cs"), Planted);
        Write(Path.Combine("bin", "Debug", "Copied.cs"), Planted);
        Write("README.md", Planted);

        Assert.Empty(RuleIdLiterals.Violations([project], RuleIds.All));
    }

    [Fact]
    public void Every_id_on_a_line_is_its_own_violation_in_nested_folders_of_every_project()
    {
        string second = Directory.CreateTempSubdirectory("rule-id-literals-").FullName;
        try
        {
            Write(Path.Combine("Endpoints", "Gate.cs"), ["string[] ids = [\"gate.origin\", \"gate.method\"];"]);
            File.WriteAllLines(Path.Combine(second, "Session.cs"), ["", "var rule = \"auth.session\";"]);

            Assert.Equal(
                [
                    $"{Path.GetFileName(project)}/Endpoints/Gate.cs:1: \"gate.origin\"",
                    $"{Path.GetFileName(project)}/Endpoints/Gate.cs:1: \"gate.method\"",
                    $"{Path.GetFileName(second)}/Session.cs:2: \"auth.session\"",
                ],
                RuleIdLiterals.Violations([project, second], RuleIds.All));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void A_project_directory_that_is_not_there_is_an_error_and_not_a_clean_tree()
    {
        // A scan over a directory that does not exist reads no file, and an empty answer from it would
        // look exactly like a project that holds the rule.
        string missing = Path.Combine(project, "Platform.Missing");

        Assert.Throws<DirectoryNotFoundException>(() => RuleIdLiterals.Violations([missing], RuleIds.All).ToList());
    }

    public void Dispose() => Directory.Delete(project, recursive: true);

    private void Write(string relativePath, string[] lines)
    {
        string path = Path.Combine(project, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
