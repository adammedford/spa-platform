using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed fragments class (#1005, T14 of #961): the G9 sitting's rows that no hermetic test can
/// stand in for, one test per row, each the driver (<see cref="FragmentsDriver"/>) and the observer
/// (<see cref="FragmentsObserver"/>) with the row's evidence in <c>SPA_E2E_EVIDENCE</c>, beside #358's
/// continuity class and in its form. With <c>SPA_E2E_STACK</c> unset or <c>local</c> every row reports
/// skipped, naming the switch; the observer's rules are proved against recordings in
/// <see cref="FragmentsObserverTests"/> and the run's in <see cref="FragmentsRunTests"/>, which run
/// everywhere and need no AWS credentials.
///
/// <para>A sitting runs one row per invocation, selected by its method, as wiz runs the continuity class:
/// <c>dotnet test --project tests/e2e -- --filter-method "Gateway.E2ETests.Deployed.FragmentsTests.G9_O5_*"</c>,
/// with <c>SPA_E2E_STACK=deployed</c>, <c>SPA_E2E_EVIDENCE</c> naming an ignored directory, and
/// <c>SPA_G9_SHELL</c> naming the shell whose live entry sets N (app1 when unset). F-31 reads
/// <c>SPA_G9_PRE_IMAGE_COMMIT</c>, <c>SPA_G9_PRE_IMAGE_DIGEST</c> and <c>SPA_G9_FRAGMENTS_IMAGE_DIGEST</c>, and
/// O4 <c>SPA_G9_O4_TABLE</c>.</para>
///
/// <para>The id-bearing methods carry the names contracts/test-ids.md gives them (F-31, F-37, F-39 to F-42,
/// N-38 and N-46); the record rows, O1, O2, O4 and O9, have no id and carry their obligation in their name.
/// A not-proved row is a red test whose message starts <c>not proved:</c>; a broken rule's starts
/// <c>failed:</c>. O3, O5, O9 and O13's refusals still report <c>not proved: the driver cannot yet …</c>,
/// naming the part (#1411): O3 waits on #1529's seam and O9 on #1530's fixture.</para>
///
/// <para>The drives pull the cell's levers they need and put them back (#1411's decision of 2026-11-24):
/// O1 writes the shell's entry at each N with spactl, so <c>Policy:FragmentsPerShell</c> is raised to 6 for its
/// run (#1011 row 1); O2 scales the gateway service to one task and two; F-T5 activates the winning fragment
/// release. O1, O2 and F-T5 read <c>SPA_E2E_PLATFORM_CONFIG</c> and the outputs document
/// <c>SPA_E2E_OUTPUTS</c> names (the repository's when unset). The browser rows launch Chromium, Firefox and
/// WebKit through a loopback proxy to the tunnel; an engine that does not launch is named on standard output
/// and missing from the record.</para>
/// </summary>
public sealed class FragmentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G9_O1_redirects_and_the_cap_are_recorded_in_each_engine_at_N_0_to_6()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O1, control,
            (_, ct) => driver.RedirectsAsync(ct),
            (driven, _) => FragmentsObserver.Redirects(driven.Record),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O2_refresh_latency_and_write_contention_are_recorded_on_one_task_and_two()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O2, control,
            (_, ct) => driver.ContentionAsync(ct),
            (driven, capture) => FragmentsObserver.Contention(FragmentsWorkloads.WithWrites(driven.Record, driven.Steps, capture)),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O3_a_successor_obtained_during_logout_is_refused()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O3, control,
            (_, ct) => driver.NotDrivenAsync(new SuccessorRecord("", "", null, null),
                "present the successor a concurrent refresh obtained to the provider after the logout: the token never leaves the gateway task, and the seam that would reach it is #1529", ct),
            (driven, capture) => FragmentsObserver.Successor(driven.Steps, driven.Record, capture),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O4_the_record_size_and_write_capacity_are_recorded()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O4, control,
            (_, ct) => driver.RecordSizeAsync(ct),
            (driven, capture) => FragmentsObserver.Size(driven.Record with
            {
                Capacity = driven.Record.Capacity is { } sample ? FragmentsWorkloads.Isolated(sample, driven.Steps, capture) : null,
            }),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O5_the_hashed_import_map_cases_in_each_browser()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O5, control,
            (_, ct) => driver.NotDrivenAsync(new ImportMapRecord([]),
                "publish a shell release carrying the four hashed import-map case pages, write their hashes into the entry's script-src, and load each case in each engine", ct),
            (driven, _) => FragmentsObserver.ImportMap(driven.Record, driven.Context.UserAgents),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O8_the_fallback_is_page_less_inside_the_managed_login_hour_and_prompts_after_it()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O8, control,
            (_, ct) => driver.FallbackAsync(ct),
            (driven, _) => FragmentsObserver.Fallback(driven.Record),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O9_header_limits_are_recorded_against_the_merged_csp_at_the_cap()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O9, control,
            (_, ct) => driver.NotDrivenAsync(new HeaderLimitRecord(null, null, null, null, []),
                "probe the largest response header the ALB, Kestrel and each engine accept: nothing on the cell answers a header of a chosen size, and the fixture that would is #1530", ct),
            (driven, _) => FragmentsObserver.HeaderLimits(driven.Record, driven.Context.UserAgents),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O10_F_T5_two_racing_publishes_on_the_cell_s_bucket_complete_once()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.FT5, control,
            (_, ct) => driver.PublishRaceAsync(ct),
            (driven, _) => FragmentsObserver.PublishRaces(driven.Record),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O12_the_entry_module_imports_and_its_relative_chunk_resolves()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O12, control,
            (_, ct) => driver.EntryImportsAsync(ct),
            (driven, _) => FragmentsObserver.EntryImports(driven.Record),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O13_the_chain_completes_and_each_step_s_fetch_metadata_is_recorded()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O13Chain, control,
            (_, ct) => driver.ChainAsync(ct),
            (driven, _) => FragmentsObserver.Chain(driven.Record, driven.Context),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_O13_a_replayed_foreign_or_expired_step_lands_on_root()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.O13Refusal, control,
            (_, ct) => driver.NotDrivenAsync(new ChainRefusalRecord([]),
                "read the session record's version before and after a replayed, a foreign and an expired /bff/chain step, each held from a sign-in in each engine", ct),
            (driven, _) => FragmentsObserver.ChainRefusals(driven.Record, driven.Context.UserAgents),
            Ct);
        result.Assert();
    }

    [Fact]
    public async Task G9_a_pre_fragments_build_refuses_a_schemaVersion_2_document_across_a_rolling_deploy()
    {
        await using FragmentsDriver driver = await FragmentsDriver.StartAsync();
        Step control = await driver.ControlAsync(Ct);
        RowResult result = await driver.Run.RowAsync(FragmentsBounds.Row10, control,
            (_, ct) => driver.RollingRefusalAsync(FragmentsBounds.Of(FragmentsBounds.Row10).Wall - TimeSpan.FromMinutes(5), ct),
            (driven, capture) => FragmentsObserver.RollingRefusal(driven.Record, capture),
            Ct);
        result.Assert();
    }
}
