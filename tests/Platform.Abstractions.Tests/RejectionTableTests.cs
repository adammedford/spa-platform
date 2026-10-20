using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="Problems.Rows"/> and <see cref="Rejection.Rows"/> against the two contract tables they
/// copy: contracts/http-contract.md -> Errors, and the <c>code</c> column of
/// contracts/request-gate.md -> Rule ids. The join between a rule id and its status and code is held
/// here once (#142, Q8), so a stage that ends a rejection by rule id agrees with both contracts by
/// construction. <c>RuleIdsTests</c> is the same guard for the rule ids themselves.
///
/// Both tables are asserted as sets: nothing walks either <c>Rows</c> in order, so a cosmetic reorder
/// of a contract table must not fail a test (#142, user story 31).
/// </summary>
public sealed class RejectionTableTests
{
    [Fact]
    public void Problems_rows_are_the_Errors_table_as_a_set()
    {
        // Mutation: swap the 502 and 504 codes in Problems.Rows.
        Assert.Equal(
            ContractTables.Errors().ToHashSet(),
            Problems.Rows.ToHashSet());
        Assert.Equal(Problems.Rows.Length, Problems.Rows.ToHashSet().Count);
    }

    [Fact]
    public void Every_Problems_constant_is_a_row_s_code_and_every_code_a_constant()
    {
        // Mutation: add a twelfth constant without a row, or a row whose code is a literal of its
        // own. Without this a constant could name a code the table test never sees, and a stage
        // spelling it would write a body the contract does not list.
        string[] declared =
        [
            .. typeof(Problems)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(declared, Problems.Rows.Select(row => row.Code).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_Errors_reader_ignores_other_sections_and_table_headings()
    {
        // Mutation: drop the section check, and the planted rows either side are read.
        string[] lines =
        [
            "## Other section",
            "| 418 | `wrong_before` | ignored |",
            "## Errors",
            "| Status | `code` | When |",
            "| --- | --- | --- |",
            "| 400 | `invalid_request` | First |",
            "| 504 | `upstream_timeout` | Last |",
            "## Following section",
            "| 419 | `wrong_after` | ignored |",
        ];

        Assert.Equal([(400, "invalid_request"), (504, "upstream_timeout")], ContractTables.ParseErrors(lines));
    }

    [Fact]
    public void A_missing_Errors_section_cannot_pass_as_an_empty_contract()
    {
        // Mutation: drop the parser's Assert.NotEmpty, and a renamed heading passes as no rows.
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ContractTables.ParseErrors(["## Errors", "| Status | `code` | When |", "| --- | --- | --- |"]));
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ContractTables.ParseErrors(["## Other section"]));
    }

    /// <summary>
    /// The <c>code</c> column of contracts/request-gate.md -> Rule ids, paired by hand.
    ///
    /// Why by hand: #159's change request decided the column lands between gates
    /// (contracts/changes/issue-160-rejection-table.md, "Landing it between gates"), but committing a
    /// frozen contract's table is the integrator's act and it is not in contracts/request-gate.md
    /// yet. #160 says to pair by hand until it lands, and to say so here. These cells are the change
    /// request's column verbatim, <c>—</c> included. Once the column is in the contract, this list
    /// goes and the test below reads <see cref="ContractTables.ParseRuleCodes"/> over
    /// <see cref="ContractTables.RequestGate"/> instead, the parser the planted-table tests already
    /// prove.
    /// </summary>
    private static readonly (string Rule, string Code)[] CodeColumn =
    [
        ("host.unknown", "not_found"),
        ("path.invalid", "invalid_request"),
        ("path.reserved", "not_found"),
        ("gate.cookie-name", "invalid_request"),
        ("gate.fetch-site", "origin_rejected"),
        ("gate.origin", "origin_rejected"),
        ("gate.method", "method_not_allowed"),
        ("gate.navigation", "origin_rejected"),
        ("csrf.token", "csrf_failed"),
        ("auth.callback", "—"),
        ("auth.session", "unauthenticated"),
        ("api.unknown", "unknown_api"),
    ];

    [Fact]
    public void Rejection_rows_are_the_code_column_by_rule_id()
    {
        // Mutation: change gate.origin's code in Rejection.Rows to csrf_failed.
        Assert.Equal(
            CodeColumn.Where(row => row.Code != "—").ToHashSet(),
            Rejection.Rows.ToHashSet());
        Assert.Equal(Rejection.Rows.Length, Rejection.Rows.Select(row => row.Rule).Distinct().Count());
    }

    [Fact]
    public void The_hand_paired_column_covers_every_rule_id_once()
    {
        // The hand pairing is keyed by the contract's rule ids: RuleIdsTests holds RuleIds.All to the
        // table's first column, so an id added there without a cell here fails this test.
        Assert.Equal(
            RuleIds.All.Order(StringComparer.Ordinal).ToArray(),
            CodeColumn.Select(row => row.Rule).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Every_Rejection_code_is_a_Problems_row()
    {
        // Mutation: add ("api.unknown", "unknown_upstream") to Rejection.Rows. A rule whose code is
        // not in the Errors table would end a request with a body the contract does not list.
        HashSet<string> codes = [.. Problems.Rows.Select(row => row.Code)];
        Assert.All(Rejection.Rows, row => Assert.Contains(row.Code, codes));
    }

    [Fact]
    public void The_code_column_reader_ignores_other_sections_headings_and_the_dash()
    {
        // The widened table as #159 decided it: the code cell appended last, and — for an id that
        // ends on no problem body. Mutation: drop the section check, or read the dash as a code.
        string[] lines =
        [
            "## Other section",
            "| `wrong.before` | ignored | `not_found` |",
            "## Rule ids",
            "| Rule id | Fires when | `code` |",
            "| --- | --- | --- |",
            "| `host.unknown` | First | `not_found` |",
            "| `auth.callback` | Any refused login callback | — |",
            "| `api.unknown` | Last | `unknown_api` |",
            "## Following section",
            "| `wrong.after` | ignored | `not_found` |",
        ];

        Assert.Equal(
            [("host.unknown", "not_found"), ("api.unknown", "unknown_api")],
            ContractTables.ParseRuleCodes(lines));
    }

    [Fact]
    public void The_code_column_reader_refuses_an_empty_table_and_a_table_without_the_column()
    {
        // Mutation: drop the parser's Assert.NotEmpty, or its check that each row has a code cell.
        // The second is today's contract, whose table has not gained the column yet.
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ContractTables.ParseRuleCodes(["## Rule ids", "| Rule id | Fires when | `code` |", "| --- | --- | --- |"]));
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ContractTables.ParseRuleCodes(["## Other section"]));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ContractTables.ParseRuleCodes(
            ["## Rule ids", "| Rule id | Fires when |", "| --- | --- |", "| `host.unknown` | First |"]));
    }
}
