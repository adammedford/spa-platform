using System.Reflection;
using System.Runtime.CompilerServices;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="RuleIds"/> against contracts/request-gate.md, which is the normative list. ADR-0015
/// moved the strings here so that four workstreams stop keeping copies of them; this test is what
/// makes that safe, because a copy nobody checks is how the gateway comes to refuse a request under
/// an id the contract does not list.
///
/// It reads the contract's own table rather than a second list of ids, so there is one place to
/// edit and the edit cannot be half-made.
/// </summary>
public sealed class RuleIdsTests
{
    [Fact]
    public void The_constants_are_exactly_the_contract_s_table_in_its_order()
    {
        Assert.Equal(ContractRuleIds(), RuleIds.All);
    }

    [Fact]
    public void Every_public_constant_is_in_All()
    {
        // A constant added without a line in All would be invisible to the test above, and to the
        // negative suite that walks All.
        string[] declared =
        [
            .. typeof(RuleIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Order(),
        ];

        Assert.Equal(declared, RuleIds.All.Order().ToArray());
    }

    [Fact]
    public void No_id_appears_twice()
    {
        Assert.Equal(RuleIds.All.Length, RuleIds.All.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(EveryRuleId))]
    public void Every_id_is_lower_case_dotted_with_one_dot(string id)
    {
        // The shape telemetry.md assumes when it tags security.rejections by rule: a prefix naming
        // the stage that refused, then the rule. A stray capital would split one counter in two.
        Assert.Equal(id.ToLowerInvariant(), id);
        Assert.Equal(1, id.Count(character => character == '.'));
        Assert.DoesNotContain(' ', id);
    }

    public static TheoryData<string> EveryRuleId() => new(RuleIds.All);

    /// <summary>
    /// The first cell of every row under the contract's "## Rule ids" heading, in the order the
    /// table gives them.
    /// </summary>
    private static string[] ContractRuleIds()
    {
        string[] lines = File.ReadAllLines(Path.Combine(ContractsDirectory(), "request-gate.md"));
        List<string> ids = [];
        bool inSection = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = line.Trim() == "## Rule ids";
                continue;
            }

            if (!inSection || !line.StartsWith('|'))
            {
                continue;
            }

            string cell = line.Split('|')[1].Trim();
            if (cell is "Rule id" || cell.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            ids.Add(cell.Trim('`'));
        }

        Assert.NotEmpty(ids);
        return [.. ids];
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
