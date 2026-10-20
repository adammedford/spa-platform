using System.Reflection;
using System.Text.RegularExpressions;
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

    [Fact]
    public void Callers_cannot_change_the_shared_catalogue()
    {
        // All is shared across owners (ADR-0015), so one consumer must not rewrite another's ids.
        IList<string> ids = RuleIds.All;
        Assert.Throws<NotSupportedException>(() => ids[0] = "host.changed");
        Assert.Equal(RuleIds.HostUnknown, RuleIds.All[0]);
    }

    [Theory]
    [MemberData(nameof(EveryRuleId))]
    public void Every_id_is_lower_case_dotted_with_one_dot(string id)
    {
        // The shape telemetry.md assumes when it tags security.rejections by rule: a prefix naming
        // the stage that refused, then the rule. A stray capital would split one counter in two.
        Assert.True(IsRuleId(id));
    }

    [Theory]
    [InlineData("Host.unknown")]
    [InlineData("host.unknown.extra")]
    [InlineData("host.unknown\t")]
    [InlineData("host.unknown\n")]
    [InlineData("host.")]
    [InlineData(".unknown")]
    [InlineData("host.unknown!")]
    [InlineData("host.unknown--rule")]
    public void The_shape_check_rejects_malformed_ids(string id)
    {
        Assert.False(IsRuleId(id));
    }

    [Fact]
    public void The_table_reader_ignores_other_sections_and_table_headings()
    {
        // Prove the drift guard reads only the normative section, including its final row.
        string[] lines =
        [
            "## Other section",
            "| `wrong.before` | ignored |",
            "## Rule ids",
            "| Rule id | Fires when |",
            "| --- | --- |",
            "| `host.unknown` | First |",
            "| `api.unknown` | Last |",
            "## Following section",
            "| `wrong.after` | ignored |",
        ];
        Assert.Equal(["host.unknown", "api.unknown"], ParseRuleIds(lines));
    }

    [Fact]
    public void A_missing_rule_section_cannot_pass_as_an_empty_contract()
    {
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ParseRuleIds(["## Other section"]));
    }

    private static bool IsRuleId(string id) =>
        Regex.IsMatch(id, @"\A[a-z]+\.[a-z]+(?:-[a-z]+)*\z", RegexOptions.CultureInvariant);

    public static TheoryData<string> EveryRuleId() => new(RuleIds.All);

    /// <summary>
    /// The first cell of every row under the contract's "## Rule ids" heading, in the order the
    /// table gives them.
    /// </summary>
    private static string[] ContractRuleIds()
    {
        return ParseRuleIds(ContractTables.RequestGate());
    }

    private static string[] ParseRuleIds(IEnumerable<string> lines)
    {
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
}
