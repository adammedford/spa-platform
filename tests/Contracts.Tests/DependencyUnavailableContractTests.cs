namespace Contracts.Tests;

/// <summary>
/// The browser contract's "When a dependency is unavailable" table (change request #543, decided
/// https://github.com/adammedford/spa-platform/issues/543#issuecomment-6595705178, accepting the draft
/// at https://github.com/adammedford/spa-platform/issues/543#issuecomment-6554274281): what the gateway
/// answers, logs and keeps when a dependency it reads cannot be read. The table lands only the rows
/// whose behaviour a landed test proves, so each row here names that behaviour; the owners' own tests
/// (<c>tests/Platform.Hosting.Tests</c>, <c>tests/Platform.Registry.Tests</c>,
/// <c>tests/Platform.Identity.Tests</c>) prove it, and this project reads only the contract.
/// </summary>
public sealed class DependencyUnavailableContractTests
{
    private const string ErrorsHeading = "## Errors";
    private const string TableHeading = "## When a dependency is unavailable";

    [Fact]
    public void The_table_follows_the_errors_section()
    {
        // Mutation: put the section before Errors, and this fails on the order; put a heading between
        // them, and it fails on the heading in between.
        string text = ContractFiles.ReadText("http-contract.md");

        int errors = text.IndexOf(ErrorsHeading + "\n", StringComparison.Ordinal);
        int table = text.IndexOf(TableHeading + "\n", StringComparison.Ordinal);

        Assert.True(errors >= 0, $"http-contract.md has no '{ErrorsHeading}' heading.");
        Assert.True(table >= 0, $"http-contract.md has no '{TableHeading}' heading.");
        Assert.True(table > errors, $"'{TableHeading}' comes before '{ErrorsHeading}'.");
        Assert.Equal(-1, text.IndexOf("\n#", errors + 1, table - errors - 1, StringComparison.Ordinal));
    }

    [Fact]
    public void The_preamble_points_at_the_cases_contracted_elsewhere_rather_than_restating_them()
    {
        // The accepted draft replaces the preamble's last sentence, which said a case with no row has
        // no promised answer, with pointers to where the others are contracted. Mutation: put the old
        // sentence back, and this fails on it; drop a pointer, and it fails on that pointer.
        string section = Section(ContractFiles.ReadText("http-contract.md"), TableHeading);
        string preamble = string.Join(' ', section[..section.IndexOf("\n|", StringComparison.Ordinal)]
            .Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries));

        Assert.DoesNotContain("a case with no row has no answer", preamble);
        Assert.Contains("the codes in Errors above, `idp_unavailable` among them", preamble);
        Assert.Contains("in `contracts/upstream-contract.md`", preamble);
        Assert.Contains("which leaves the live snapshot serving (ADR-0002)", preamble);
        Assert.Contains("A case named in none of these has no answer the contract promises yet.", preamble);
    }

    [Fact]
    public void The_store_outage_is_an_errors_row_and_not_yet_a_table_row()
    {
        // The draft's item 2 lands the code's registration in Errors; its table row waits for
        // workstream 2's proving test. Mutation: drop the Errors row, or give it another status, and
        // this fails; the table's own absence of the code is The_table_lands_no_row_the_decision_left_out.
        string errors = Section(ContractFiles.ReadText("http-contract.md"), ErrorsHeading);

        Assert.Contains(
            "| 503 | `store_unavailable` | The object store could not be read to serve an asset or the shell, for a reason other than a refusal; the live snapshot is unaffected |",
            errors);
    }

    [Fact]
    public void Each_row_states_the_answer_the_log_and_the_snapshot()
    {
        // Mutation: drop a column from the header, or a cell from a row, and this fails on the count.
        IReadOnlyList<IReadOnlyList<string>> rows = Rows(Section(ContractFiles.ReadText("http-contract.md"), TableHeading));

        Assert.Equal(["Dependency, case", "Answer", "Logged", "Snapshot"], rows[0]);
        Assert.All(rows, row => Assert.Equal(4, row.Count));
        Assert.All(rows.Skip(1), row => Assert.All(row, cell => Assert.NotEqual("", cell)));
    }

    [Fact]
    public void A_refused_asset_read_is_404_and_a_refused_shell_500_as_issue_381_decided()
    {
        // Mutation: write the draft's 503 store_unavailable for a refused read, and this fails on both
        // answers; drop the shell's answer, and it fails on shell_unavailable; drop "no rule id", the
        // shell's "with the same header", the asset or shell label, "never the key", the shell's Error
        // clause or the Snapshot cell, and it fails on each.
        string row = Row(TableHeading, "The object store refuses a read");

        Assert.Contains("`404` `not_found`", row);
        Assert.Contains("`500` `shell_unavailable` with the same header", row);
        Assert.Contains("`Cache-Control: private, no-store`, no rule id", row);
        Assert.Contains("`read_refused`, `asset` or `shell`", row);
        Assert.Contains("never the key", row);
        Assert.Contains("one Error naming the app, the release and that the store refused", row);
        Assert.Equal("Stays", Cells(TableHeading, "The object store refuses a read")[3]);
    }

    [Fact]
    public void Parameter_store_unreadable_at_a_cold_start_leaves_the_gateway_not_ready_and_every_host_unknown()
    {
        // Mutation: say /readyz answers 200 or the app paths answer 503, and this fails; change the
        // Logged clause for each unreadable secret or for the revision, or the Snapshot cell, and it
        // fails on that.
        string row = Row(TableHeading, "Parameter Store cannot be read at a cold start");

        Assert.Contains("`/readyz` stays `503`", row);
        Assert.Contains("`404` `not_found`", row);
        Assert.Contains("One Warning per app whose secret cannot be read, naming the app and the reference, never the value", row);
        Assert.Contains("one Error per poll that the revision is not applied", row);
        Assert.Equal(
            "There is none, and each poll reads the document again",
            Cells(TableHeading, "Parameter Store cannot be read at a cold start")[3]);
    }

    [Fact]
    public void A_session_store_at_its_bound_ends_the_session_nearest_its_expiry()
    {
        // Mutation: write the draft's Information line, which MemorySessionStore does not log, and this
        // fails on the Logged cell; say the login is refused, and it fails on the answer; say the newest
        // session is ended, and it fails on the eviction rule.
        IReadOnlyList<string> cells = Cells(TableHeading, "The in-memory session store is at `Session:MaxSessions`");

        Assert.Contains("The login succeeds", cells[1]);
        Assert.Contains("the session nearest its expiry, an expired one first, is ended", cells[1]);
        Assert.Contains("that session's next request is `401` `unauthenticated`", cells[1]);
        Assert.Equal("Nothing at the eviction", cells[2]);
        Assert.Equal("Stays", cells[3]);
    }

    [Fact]
    public void The_table_lands_no_row_the_decision_left_out()
    {
        // The decision leaves out the P2-1, P2-5 and P2-7 rows, each to land with its own request, and
        // lands no code the Errors table does not hold. Mutation: add the draft's session-store,
        // task-metadata or telemetry row, or its store_unavailable code, and this fails.
        string section = Section(ContractFiles.ReadText("http-contract.md"), TableHeading);

        Assert.DoesNotContain("P2-1", section);
        Assert.DoesNotContain("P2-5", section);
        Assert.DoesNotContain("P2-7", section);
        Assert.DoesNotContain("store_unavailable", section);
    }

    private static string Row(string heading, string firstCell) => string.Join(" | ", Cells(heading, firstCell));

    /// <summary>The cells of the one row whose first cell starts with <paramref name="firstCell"/>: dependency, answer, logged, snapshot.</summary>
    private static IReadOnlyList<string> Cells(string heading, string firstCell)
    {
        IReadOnlyList<IReadOnlyList<string>> rows = Rows(Section(ContractFiles.ReadText("http-contract.md"), heading));
        IReadOnlyList<string>[] matched = [.. rows.Where(row => row[0].StartsWith(firstCell, StringComparison.Ordinal))];
        Assert.True(matched.Length == 1, $"'{heading}' has {matched.Length} rows starting '{firstCell}'.");
        return matched[0];
    }

    /// <summary>The section's table rows, the header first and the separator left out, each as its trimmed cells.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> Rows(string section)
    {
        IReadOnlyList<string>[] rows = [.. section.Split('\n')
            .Where(line => line.StartsWith('|'))
            .Where(line => !line.StartsWith("| ---", StringComparison.Ordinal))
            .Select(line => (IReadOnlyList<string>)[.. line.Trim().Trim('|').Split(" | ").Select(cell => cell.Trim())])];
        Assert.True(rows.Length > 1, "The section holds no table rows.");
        return rows;
    }

    private static string Section(string text, string heading)
    {
        int start = text.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"http-contract.md has no '{heading}' heading.");
        int next = text.IndexOf("\n#", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }
}
