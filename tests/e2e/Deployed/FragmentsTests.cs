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
/// <c>failed:</c>. A row whose part this driver cannot yet drive reports <c>not proved: the driver cannot yet …</c>
/// naming the part.</para>
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
            (_, ct) => driver.NotDrivenAsync(new RedirectRecord([]),
                "drive Chromium, Firefox and WebKit through Cognito's managed login at N = 0 to 6 and record each redirect: the project installs Chromium alone (docs/dependencies.md)", ct),
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
            (_, ct) => driver.NotDrivenAsync(new RefreshContention([], null),
                "read each refresh's latency and each write's attempts and CAS conflicts at 1 + 5 due sets: the gateway's lines carry neither (contracts/telemetry.md)", ct),
            (driven, _) => FragmentsObserver.Contention(driven.Record),
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
                "present the successor a concurrent refresh obtained to the provider after the logout: the gateway keeps every token on the server, so the probe needs a way to it this class does not have", ct),
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
            (driven, _) => FragmentsObserver.Size(driven.Record),
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
                "load the four hashed import-map cases in each sitting browser: it needs a shell release carrying them on the cell, and Firefox and WebKit", ct),
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
            (_, ct) => driver.NotDrivenAsync(new FallbackRecord(null, null, null),
                "open a second client's /bff/login?fragment= on the shell's host inside and after Cognito's managed-login hour", ct),
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
                "probe the largest response header the ALB, Kestrel and each sitting browser accept beside the merged CSP at the cap", ct),
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
            (_, ct) => driver.NotDrivenAsync(new PublishRaceRecord([]),
                "hold two spactl fragment publish runs of one key at a barrier before the claim, which this driver does not do", ct),
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
            (_, ct) => driver.NotDrivenAsync(new EntryImportRecord([]),
                "import a fragment's entry at entry.js and at another name in the browser and record its chunk's resolved path", ct),
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
            (_, ct) => driver.NotDrivenAsync(new ChainRecord([]),
                "sign in at N = 2 in each sitting browser and record each /bff/chain request's Sec-Fetch-Site, -Mode and -Dest", ct),
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
                "replay a /bff/chain step after the chain, with another session's continuation and past ChainLifetime, reading the session record's version before and after each", ct),
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
