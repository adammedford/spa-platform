using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The fragments observer's rules (#1005), one at a time: each row's recording that satisfies it
/// passes, and one recording per rule reports failed or not proved, never passed. The rows that read
/// the AWS CLI read it from a <see cref="RecordedRunner"/>, so no test here needs AWS credentials.
/// Each test names in its body the mutation of the observer that turns it red.
/// </summary>
public sealed class FragmentsObserverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static Cell Cell => Cell.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")));

    internal const string TaskA = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";
    internal const string TaskB = "9f8e7d6c5b4a43210fedcba987654321";

    internal static readonly IReadOnlyList<string> Agents = ["chromium 131", "firefox 133"];

    private static void AssertNotPassed(RowResult result, string reason)
    {
        Assert.NotEqual(Verdict.Passed, result.Verdict);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        Assert.StartsWith(result.Verdict == Verdict.Failed ? "failed: " : "not proved: ", result.Message, StringComparison.Ordinal);
    }

    // ---- O1, redirects and the cap (record) ----

    private static IReadOnlyList<Redirect> Redirects(int count) => [.. Enumerable.Range(0, count).Select(i => new Redirect(302, $"/hop/{i}"))];

    /// <summary>
    /// A run per N as TestIdp's 4 + 3N would give it, split at no page; at N = 6 (22) the browser follows
    /// 20 and stops short of returnUrl.
    /// </summary>
    internal static RedirectRecord GoodRedirects() => new([.. FragmentsObserver.Engines.Select(engine => new EngineRedirects(engine, "131.0", [.. Enumerable.Range(0, FragmentsObserver.MaxN + 1).Select(n => new RedirectRun(
        n,
        [Redirects(Math.Min(4 + (3 * n), FragmentsObserver.FetchRedirectLimit))],
        [Redirects(2), Redirects(2 + (3 * n))],
        [.. Enumerable.Range(0, n).Select(_ => (IReadOnlyList<IReadOnlyList<Redirect>>)[Redirects(3)])],
        4 + (3 * n) <= FragmentsObserver.FetchRedirectLimit))], new BoundaryProbe(20, 21)))]);

    private static RedirectRecord WithRun(RedirectRecord record, string engine, int n, Func<RedirectRun, RedirectRun> change) =>
        new([.. record.Engines.Select(each => each.Engine != engine ? each : each with { Runs = [.. each.Runs.Select(run => run.N == n ? change(run) : run)] })]);

    [Fact]
    public void O1_a_complete_record_passes_with_the_cap_the_engines_share()
    {
        // Mutation: making FragmentsObserver.Redirects take the smallest completing N (completing.Min()) fails this test.
        RowResult result = FragmentsObserver.Redirects(GoodRedirects());

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains("cap: N=5", string.Join('\n', result.Observations), StringComparison.Ordinal);
        Assert.Contains("margin 0, a policy choice", string.Join('\n', result.Observations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void O1_a_redirect_chain_longer_than_20_fails(bool reached)
    {
        // Mutation: deleting the FetchRedirectLimit check in FragmentsObserver.Redirects makes this pass the row.
        RowResult result = FragmentsObserver.Redirects(WithRun(GoodRedirects(), "firefox", 6, run => run with { Uninterrupted = [Redirects(22)], ReachedReturnUrl = reached }));

        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "redirect run of 22, past the Fetch standard's 20");
    }

    [Fact]
    public void O1_with_no_N_completing_in_every_engine_sets_no_cap()
    {
        // Mutation: removing the `completing.Length == 0` return from FragmentsObserver.Redirects throws on Max of nothing.
        RedirectRecord none = new([.. GoodRedirects().Engines.Select(e => e.Engine != "webkit" ? e : e with { Runs = [.. e.Runs.Select(run => run with { ReachedReturnUrl = false })] })]);

        AssertNotPassed(FragmentsObserver.Redirects(none), "no N from 0 to 6 completed in every engine");
    }

    [Theory]
    [InlineData("firefox has no run at N=3")]
    [InlineData("records 1 hop counts of 2")]
    [InlineData("webkit has no version")]
    [InlineData("chromium has no boundary probe")]
    [InlineData("has no interactive run")]
    [InlineData("has no terminal navigation")]
    public void O1_a_missing_N_hop_count_version_probe_or_run_is_not_proved(string reason)
    {
        // Mutation: returning Passed past the missing list (dropping its `missing.Count > 0` return) fails every case.
        RedirectRecord good = GoodRedirects();
        RedirectRecord broken = reason switch
        {
            "firefox has no run at N=3" => new([.. good.Engines.Select(e => e.Engine == "firefox" ? e with { Runs = [.. e.Runs.Where(run => run.N != 3)] } : e)]),
            "records 1 hop counts of 2" => WithRun(good, "chromium", 2, run => run with { Hops = [run.Hops![0]] }),
            "webkit has no version" => new([.. good.Engines.Select(e => e.Engine == "webkit" ? e with { Version = "" } : e)]),
            "chromium has no boundary probe" => new([.. good.Engines.Select(e => e.Engine == "chromium" ? e with { Probe = null } : e)]),
            "has no interactive run" => WithRun(good, "webkit", 1, run => run with { Interactive = null }),
            _ => WithRun(good, "webkit", 4, run => run with { ReachedReturnUrl = null }),
        };

        AssertNotPassed(FragmentsObserver.Redirects(broken), reason);
        Assert.Equal(Verdict.NotProved, FragmentsObserver.Redirects(broken).Verdict);
    }

    [Fact]
    public void O1_a_redirect_recorded_with_its_query_fails()
    {
        // Mutation: dropping '?' from the IndexOfAny set in FragmentsObserver.Redirects passes this row.
        RowResult result = FragmentsObserver.Redirects(WithRun(GoodRedirects(), "chromium", 0, run => run with { Uninterrupted = [[new Redirect(302, "/oauth2/authorize?client_id=x")]] }));

        AssertNotPassed(result, "with its query or fragment");
    }

    // ---- O2, refresh latency and write contention (record) ----

    internal static RefreshContention GoodContention() => new(
        [
            new LatencySample(1, 6, 10, 3, [.. Enumerable.Range(1, 30).Select(i => (double)(i * 100))], [new WriteCount(1, 0, false), new WriteCount(2, 1, false)]),
            new LatencySample(2, 6, 10, 3, [.. Enumerable.Range(1, 30).Select(i => (double)(i * 120))], [new WriteCount(2, 1, false), new WriteCount(3, 2, false)]),
        ],
        new CallbackOverlap([TaskA, TaskB], [1, 2, 2], 1, 3));

    [Fact]
    public void O2_a_complete_record_passes_confirming_the_gates_and_setting_MergeBudget()
    {
        // Mutation: changing MergeBudget's `attempts.Max() + 1` to `attempts.Max()` reads 3 and fails this test.
        RowResult result = FragmentsObserver.Contention(GoodContention() with { Callbacks = new CallbackOverlap([TaskA, TaskB], [1, 3, 2], 0, 1) });

        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains("the gates and leases are confirmed; MergeBudget 4", result.Reason, StringComparison.Ordinal);
        Assert.Contains("p95 2900 ms over 30", string.Join('\n', result.Observations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no 2-task topology")]
    [InlineData("has no per-write counts")]
    [InlineData("latency sample holds 29 of the 30")]
    [InlineData("wrote on 1 task(s), not two")]
    [InlineData("no overlapping callbacks")]
    public void O2_a_missing_topology_counts_sample_or_cross_task_write_is_not_proved(string reason)
    {
        // Mutation: weakening the `wrote.Distinct(...).Count() < 2` return passes the "wrote on 1 task(s)" case, and
        // weakening the topology, per-write or sample return passes its case (M6, M51 to M53 of the Task commit).
        RefreshContention good = GoodContention();
        RefreshContention broken = reason switch
        {
            "no 2-task topology" => good with { Topologies = [good.Topologies[0]] },
            "has no per-write counts" => good with { Topologies = [good.Topologies[0] with { Writes = [] }, good.Topologies[1]] },
            "latency sample holds 29 of the 30" => good with { Topologies = [good.Topologies[0], good.Topologies[1] with { LatenciesMs = [.. good.Topologies[1].LatenciesMs!.Skip(1)] }] },
            "wrote on 1 task(s), not two" => good with { Callbacks = good.Callbacks! with { TasksWrote = [TaskA, TaskA] } },
            _ => good with { Callbacks = null },
        };

        RowResult result = FragmentsObserver.Contention(broken);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        AssertNotPassed(result, reason);
    }

    [Theory]
    [InlineData("past the write deadline")]
    [InlineData("budget(s) exhausted")]
    [InlineData("a revision to #961 is required")]
    public void O2_flags_a_deadline_miss_an_exhausted_budget_and_a_callback_bound_with_no_headroom(string flag)
    {
        // Mutation: removing the `flags.Add` of the deadline and budget check, or of the elapsed-bound check, leaves the
        // record confirming the gates and fails this test.
        RefreshContention good = GoodContention();
        RefreshContention flagged = flag switch
        {
            "past the write deadline" => good with { Topologies = [good.Topologies[0] with { LatenciesMs = [.. good.Topologies[0].LatenciesMs!.Take(29), 20_001] }, good.Topologies[1]] },
            "budget(s) exhausted" => good with { Topologies = [good.Topologies[0], good.Topologies[1] with { Writes = [new WriteCount(3, 3, true)] }] },
            // MergeBudget max(3, 4 + 1) = 5: 25 s for the attempts alone, plus one outer get and 3 s, past 25 s.
            _ => good with { Callbacks = new CallbackOverlap([TaskA, TaskB], [4], 1, 3) },
        };

        RowResult result = FragmentsObserver.Contention(flagged);
        Assert.Contains("not confirmed", result.Reason, StringComparison.Ordinal);
        Assert.Contains(result.Observations, line => line.StartsWith("flag: ", StringComparison.Ordinal) && line.Contains(flag, StringComparison.Ordinal));
    }

    // ---- O4, DynamoDB size and capacity (record), read through the AWS CLI ----

    private const string Table = "spa-poc-o4-sessions";

    /// <summary>
    /// A scan answer: a lease item and a session item whose sizes are, by DynamoDB's rules, pk 2 + 15,
    /// appId 5 + 4, version 7 + 2, ticket 6 + 300, format 6 + 2, flag 4 + 1, tags 4 + 3 + 2 * (1 + 3), 369 in all.
    /// </summary>
    private static string Scan() => new JsonObject
    {
        ["Items"] = new JsonArray(
            new JsonObject { ["pk"] = new JsonObject { ["S"] = "lease#not-a-key" }, ["token"] = new JsonObject { ["S"] = "t" } },
            new JsonObject
            {
                ["pk"] = new JsonObject { ["S"] = "session#abcdefg" },
                ["appId"] = new JsonObject { ["S"] = "app1" },
                ["version"] = new JsonObject { ["N"] = "12" },
                ["ticket"] = new JsonObject { ["B"] = Convert.ToBase64String(new byte[300]) },
                ["format"] = new JsonObject { ["N"] = "2" },
                ["flag"] = new JsonObject { ["BOOL"] = true },
                ["tags"] = new JsonObject { ["L"] = new JsonArray(new JsonObject { ["S"] = "abc" }, new JsonObject { ["S"] = "def" }) },
            }),
    }.ToJsonString();

    private static readonly DateTimeOffset Start = new(2026, 11, 18, 2, 10, 20, TimeSpan.Zero);
    private static readonly DateTimeOffset Stop = new(2026, 11, 18, 2, 12, 40, TimeSpan.Zero);

    private static string Datapoints(params (int Minute, double Sum)[] points) => new JsonObject
    {
        ["Label"] = "ConsumedWriteCapacityUnits",
        ["Datapoints"] = new JsonArray([.. points.Select(point => (JsonNode)new JsonObject
        {
            ["Timestamp"] = new DateTimeOffset(2026, 11, 18, 2, point.Minute, 0, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["Sum"] = point.Sum,
            ["Unit"] = "Count",
        })]),
    }.ToJsonString();

    private static RecordedRunner Aws(string scan, string datapoints) => new(arguments => arguments switch
    {
        ["dynamodb", "scan", "--table-name", Table, ..] => scan,
        ["cloudwatch", "get-metric-statistics", ..] when arguments.Contains($"Name=TableName,Value={Table}") => datapoints,
        _ => throw new InvalidOperationException($"no recorded answer for aws {string.Join(' ', arguments)}"),
    });

    private static async Task<RecordSize> RecordedSizeAsync(RecordedRunner runner, IReadOnlyList<string>? others = null)
    {
        FragmentsReaders readers = new(Cell, runner);
        ItemSize size = await readers.ItemSizeAsync(Table, "ticket", Ct);
        IReadOnlyList<CapacityPeriod> periods = await readers.WriteCapacityAsync(Table, Start.AddMinutes(-2), Stop.AddMinutes(2), Ct);
        return new RecordSize(size, new CapacitySample("sign-ins", Start, Stop, 6, periods, others ?? [], []));
    }

    [Fact]
    public async Task O4_a_recorded_scan_and_capacity_sample_pass_with_stored_and_payload_bytes_apart()
    {
        // Mutation: counting a binary value by its base64 length in FragmentsReaders.ValueBytes (400 for 300 bytes) reads
        // 469 and 400, and fails this test.
        RecordSize record = await RecordedSizeAsync(Aws(Scan(), Datapoints((9, 0), (10, 2), (11, 3), (12, 2), (13, 0))));

        Assert.Equal(new ItemSize(369, 300), record.Size);
        RowResult result = FragmentsObserver.Size(record);
        Assert.Equal(Verdict.Passed, result.Verdict);
        Assert.Contains("stored item 369 bytes, serialized payload 300 bytes", result.Observations);
    }

    [Theory]
    [InlineData("no ConsumedWriteCapacityUnits period at 2026-11-18 02:11:00Z")]
    [InlineData("control period at 2026-11-18 02:13:00Z consumed 1 WCU")]
    [InlineData("fewer than one each")]
    [InlineData("no ConsumedWriteCapacityUnits period at 2026-11-18 02:13:00Z, a control period")]
    public async Task O4_missing_period_data_a_busy_control_or_an_undercount_withholds_the_verdict(string reason)
    {
        // Mutation: skipping the per-minute lookup (or the control check, or the WCU sum check) in FragmentsObserver.Size
        // passes the matching case; reading a control with no datapoint as quiet (dropping its `is not { } quiet`
        // return, the review's reversal of the implementer's decision 5) passes the last.
        string points = reason switch
        {
            "no ConsumedWriteCapacityUnits period at 2026-11-18 02:11:00Z" => Datapoints((9, 0), (10, 2), (12, 4), (13, 0)),
            "control period at 2026-11-18 02:13:00Z consumed 1 WCU" => Datapoints((9, 0), (10, 2), (11, 3), (12, 2), (13, 1)),
            "fewer than one each" => Datapoints((9, 0), (10, 1), (11, 1), (12, 1), (13, 0)),
            _ => Datapoints((9, 0), (10, 2), (11, 3), (12, 2)),
        };

        RowResult result = FragmentsObserver.Size(await RecordedSizeAsync(Aws(Scan(), points)));
        Assert.Equal(Verdict.NotProved, result.Verdict);
        AssertNotPassed(result, reason);
    }

    [Fact]
    public async Task O4_a_contaminated_or_unchecked_sample_and_a_missing_size_withhold_the_verdict()
    {
        // Mutation: removing the OtherWriters/OverlappingWorkloads checks from FragmentsObserver.Size passes the first two.
        RecordSize good = await RecordedSizeAsync(Aws(Scan(), Datapoints((10, 2), (11, 3), (12, 2))));

        AssertNotPassed(FragmentsObserver.Size(good with { Capacity = good.Capacity! with { OtherWriters = ["spactl"] } }), "contaminated");
        AssertNotPassed(FragmentsObserver.Size(good with { Capacity = good.Capacity! with { OverlappingWorkloads = null } }), "isolation was not checked");
        AssertNotPassed(FragmentsObserver.Size(good with { Capacity = null }), "no capacity sample");
        AssertNotPassed(FragmentsObserver.Size(new RecordSize(FragmentsReaders.ItemSizeOf("""{"Items":[]}""", "ticket"), good.Capacity)), "no stored-item or serialized-payload size");
    }

    [Fact]
    public void O4_a_stored_item_smaller_than_its_payload_fails()
    {
        // Mutation: removing the `stored < payload` clause from FragmentsObserver.Size passes this row.
        RowResult result = FragmentsObserver.Size(new RecordSize(new ItemSize(200, 300), null));

        Assert.Equal(Verdict.Failed, result.Verdict);
    }

    [Fact]
    public void O4_numbers_maps_and_sets_are_sized_by_dynamodbs_rules()
    {
        // Mutation: dropping the per-element byte from the "M" arm of FragmentsReaders.ValueBytes reads 6 for the map (3 + name 1 + number 2) and fails this test.
        Assert.Equal(3, FragmentsReaders.ValueBytes(System.Text.Json.JsonDocument.Parse("""{"N":"0012300"}""").RootElement));
        Assert.Equal(7, FragmentsReaders.ValueBytes(System.Text.Json.JsonDocument.Parse("""{"M":{"a":{"N":"7"}}}""").RootElement));
        Assert.Equal(6, FragmentsReaders.ValueBytes(System.Text.Json.JsonDocument.Parse("""{"SS":["abc","def"]}""").RootElement));
    }

    // ---- N, read from the live registry ----

    [Fact]
    public async Task N_is_the_count_of_fragments_the_shells_live_entry_names_and_an_absent_shell_is_refused()
    {
        // Mutation: reading `?.Count + 1 ?? 0` in FragmentsReaders.FragmentsOfAsync reads 3 and 1 and fails this test.
        string registry = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["revision"] = 42,
            ["apps"] = new JsonArray(
                new JsonObject { ["appId"] = "app1", ["fragments"] = new JsonArray(new JsonObject { ["fragmentId"] = "orders" }, new JsonObject { ["fragmentId"] = "billing" }) },
                new JsonObject { ["appId"] = "app2" }),
        }.ToJsonString();
        RecordedRunner runner = new(arguments => arguments is ["s3", "cp", var from, "-", ..] && from == $"s3://{Cell.Bucket}/{CellAws.RegistryKey}"
            ? registry
            : throw new InvalidOperationException("unexpected call"));
        FragmentsReaders readers = new(Cell, runner);

        Assert.Equal(2, await readers.FragmentsOfAsync("app1", Ct));
        Assert.Equal(0, await readers.FragmentsOfAsync("app2", Ct));
        InvalidOperationException absent = await Assert.ThrowsAsync<InvalidOperationException>(() => readers.FragmentsOfAsync("app3", Ct));
        Assert.Contains("no entry for app3", absent.Message, StringComparison.Ordinal);
    }

    // ---- O5, import-map conformance (F-39) ----

    internal static ImportMapRecord GoodMaps() =>
        new([.. Agents.SelectMany(agent => FragmentsObserver.MapCases.Select(each => new MapCase(agent, each.Key, each.Value)))]);

    [Fact]
    public void O5_every_case_in_every_user_agent_passes_and_a_missing_or_wrong_case_does_not()
    {
        // Mutation: comparing `loaded != loads` as `loaded == loads` in FragmentsObserver.ImportMap fails the passing case;
        // returning on a missing case's continue instead of NotProved passes the missing one.
        Assert.Equal(Verdict.Passed, FragmentsObserver.ImportMap(GoodMaps(), Agents).Verdict);

        ImportMapRecord missing = new([.. GoodMaps().Cases.Where(each => !(each.UserAgent == "firefox 133" && each.Case == "sha384-admits"))]);
        AssertNotPassed(FragmentsObserver.ImportMap(missing, Agents), "the map case sha384-admits is missing for firefox 133");

        ImportMapRecord loadedWrong = new([.. GoodMaps().Cases.Select(each => each.Case == "wrong-hash-blocked" && each.UserAgent == "chromium 131" ? each with { Loaded = true } : each)]);
        RowResult wrong = FragmentsObserver.ImportMap(loadedWrong, Agents);
        Assert.Equal(Verdict.Failed, wrong.Verdict);
        AssertNotPassed(wrong, "wrong-hash-blocked loaded");
        AssertNotPassed(FragmentsObserver.ImportMap(GoodMaps(), []), "names no user agent");
    }

    // ---- O8, the fallback on one host (F-40) ----

    internal static readonly DateTimeOffset SignedIn = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);

    internal static FallbackRecord GoodFallback() =>
        new(SignedIn, new FallbackHalf(SignedIn.AddMinutes(5), false, false), new FallbackHalf(SignedIn.AddMinutes(62), true, true));

    [Fact]
    public void O8_a_page_less_first_half_and_a_prompting_second_half_pass()
    {
        // Mutation: removing the `after.At - signedIn < ManagedLoginHour` return from FragmentsObserver.Fallback passes the early-second-half case.
        Assert.Equal(Verdict.Passed, FragmentsObserver.Fallback(GoodFallback()).Verdict);
        AssertNotPassed(FragmentsObserver.Fallback(GoodFallback() with { After = new FallbackHalf(SignedIn.AddMinutes(30), true, true) }), "inside the hour");
    }

    [Fact]
    public void O8_a_fallback_that_shows_a_page_inside_the_hour_fails()
    {
        // Mutation: removing the `if (showed)` return from FragmentsObserver.Fallback passes this row.
        RowResult result = FragmentsObserver.Fallback(GoodFallback() with { Inside = new FallbackHalf(SignedIn.AddMinutes(5), true, false) });

        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "showed a page inside the managed-login hour");
    }

    [Fact]
    public void O8_a_missing_half_or_one_with_no_prompt_after_the_hour_does_not_pass()
    {
        // Mutation: removing the `!prompted` return from FragmentsObserver.Fallback passes the last case.
        AssertNotPassed(FragmentsObserver.Fallback(GoodFallback() with { After = null }), "second half");
        AssertNotPassed(FragmentsObserver.Fallback(GoodFallback() with { Inside = new FallbackHalf(SignedIn.AddMinutes(61), false, false) }), "not inside the hour");
        AssertNotPassed(FragmentsObserver.Fallback(GoodFallback() with { After = new FallbackHalf(SignedIn.AddMinutes(62), false, false) }), "did not prompt");
    }

    // ---- O9, header limits (record) ----

    internal static HeaderLimitRecord GoodHeaders() =>
        new(5, 3100, 16_000, 32_000, [.. Agents.Select(agent => new BrowserHeaderLimit(agent, 250_000))]);

    [Fact]
    public void O9_a_complete_header_record_passes_and_an_incomplete_one_is_not_proved()
    {
        // Mutation: dropping the per-agent limit from FragmentsObserver.HeaderLimits's missing list passes the first broken case.
        Assert.Equal(Verdict.Passed, FragmentsObserver.HeaderLimits(GoodHeaders(), Agents).Verdict);

        AssertNotPassed(FragmentsObserver.HeaderLimits(GoodHeaders() with { Browsers = [GoodHeaders().Browsers[0]] }, Agents), "missing firefox 133's limit");
        AssertNotPassed(FragmentsObserver.HeaderLimits(GoodHeaders() with { KestrelLargestAcceptedBytes = null }, Agents), "missing Kestrel's limit");
        AssertNotPassed(FragmentsObserver.HeaderLimits(GoodHeaders() with { MergedCspBytes = null }, Agents), "missing the merged CSP's size");
    }

    // ---- O12, entry import (F-41) ----

    internal static EntryImportRecord GoodImports() => new(
    [
        new EntryImport("orders", "entry.js", true, "/fragments/orders/assets/chunk-1a2b.js"),
        new EntryImport("billing", "main.js", true, "/fragments/billing/assets/chunk-3c4d.js"),
    ]);

    [Fact]
    public void O12_both_entries_loading_with_their_chunks_under_assets_pass()
    {
        // Mutation: dropping the "another entry name" clause from FragmentsObserver.EntryImports passes the last case.
        Assert.Equal(Verdict.Passed, FragmentsObserver.EntryImports(GoodImports()).Verdict);
        AssertNotPassed(FragmentsObserver.EntryImports(new EntryImportRecord([GoodImports().Cases[0]])), "another entry name");
    }

    [Fact]
    public void O12_an_entry_that_does_not_load_fails()
    {
        // Mutation: removing the `!import.Loaded.Value` return from FragmentsObserver.EntryImports passes this row.
        RowResult result = FragmentsObserver.EntryImports(new([GoodImports().Cases[0] with { Loaded = false }, GoodImports().Cases[1]]));

        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "import('/fragments/orders/entry.js') did not load");
    }

    [Theory]
    [InlineData("/fragments/orders/chunk-1a2b.js")]
    [InlineData("/fragments/billing/assets/chunk.js")]
    [InlineData("/fragments/orders/assets/../entry.js")]
    public void O12_a_chunk_resolved_outside_the_fragments_assets_fails(string chunk)
    {
        // Mutation: testing the chunk against "/fragments/" alone in FragmentsObserver.EntryImports passes the first two cases.
        EntryImportRecord broken = new([GoodImports().Cases[0] with { ChunkPath = chunk }, GoodImports().Cases[1]]);

        RowResult result = FragmentsObserver.EntryImports(broken);
        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "outside /fragments/orders/assets/");
    }

    // ---- O13, the chain (F-42) and its refusals (N-46) ----

    internal static FragmentsContext AtTwo => new(2, Agents, [TaskA]);

    internal static ChainRecord GoodChain() => new([.. Agents.Select(agent => new ChainRun(agent,
        [new ChainStep("cross-site", "navigate", "document"), new ChainStep("same-site", "navigate", "document")], true))]);

    [Fact]
    public void O13_a_completed_chain_with_every_steps_fetch_metadata_passes()
    {
        // Mutation: removing the `context.N != 2` return from FragmentsObserver.Chain passes the N = 3 case.
        Assert.Equal(Verdict.Passed, FragmentsObserver.Chain(GoodChain(), AtTwo).Verdict);
        AssertNotPassed(FragmentsObserver.Chain(GoodChain(), AtTwo with { N = 3 }), "runs at N = 2");
        AssertNotPassed(FragmentsObserver.Chain(GoodChain(), AtTwo with { N = null }), "names no fragments");
    }

    [Fact]
    public void O13_a_chain_step_with_no_fetch_metadata_recorded_is_not_proved()
    {
        // Mutation: dropping the Mode test from the Fetch Metadata check in FragmentsObserver.Chain passes this row.
        ChainRecord broken = new([GoodChain().Runs[0], GoodChain().Runs[1] with { Steps = [new ChainStep("cross-site", null, "document"), GoodChain().Runs[1].Steps[1]] }]);

        RowResult result = FragmentsObserver.Chain(broken, AtTwo);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        AssertNotPassed(result, "has no Fetch Metadata recorded");
        Assert.Equal(Verdict.Failed, FragmentsObserver.Chain(new([GoodChain().Runs[0] with { Completed = false }, GoodChain().Runs[1]]), AtTwo).Verdict);
    }

    [Fact]
    public void O13_an_agent_with_no_chain_or_a_short_one_is_not_proved()
    {
        // Mutation: removing the `run.Steps.Count != context.N` return from FragmentsObserver.Chain passes the short chain.
        AssertNotPassed(FragmentsObserver.Chain(new([GoodChain().Runs[0]]), AtTwo), "no chain was observed in firefox 133");
        AssertNotPassed(FragmentsObserver.Chain(new([GoodChain().Runs[0], GoodChain().Runs[1] with { Steps = [GoodChain().Runs[1].Steps[0]] }]), AtTwo), "recorded 1 /bff/chain steps of 2");
    }

    internal static ChainRefusalRecord GoodRefusals() =>
        new([.. Agents.SelectMany(agent => FragmentsObserver.RefusalCases.Select(name => new ChainRefusal(agent, name, "/", 7, 7)
        {
            SinceMint = name == "expired" ? FragmentsBounds.ChainLifetime + TimeSpan.FromSeconds(30) : null,
        }))]);

    [Fact]
    public void O13_a_replayed_step_that_changes_the_record_fails()
    {
        // Mutation: removing the `after != before` return from FragmentsObserver.ChainRefusals passes this row.
        Assert.Equal(Verdict.Passed, FragmentsObserver.ChainRefusals(GoodRefusals(), Agents).Verdict);
        ChainRefusalRecord changed = new([.. GoodRefusals().Cases.Select(each => each.Case == "replayed" && each.UserAgent == "firefox 133" ? each with { VersionAfter = 8 } : each)]);

        RowResult result = FragmentsObserver.ChainRefusals(changed, Agents);
        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "the replayed step in firefox 133 changed the session record's version from 7 to 8");
    }

    [Fact]
    public void O13_a_step_landing_elsewhere_or_unobserved_does_not_pass()
    {
        // Mutation: removing the `landed != "/"` return from FragmentsObserver.ChainRefusals passes the first case.
        AssertNotPassed(FragmentsObserver.ChainRefusals(new([.. GoodRefusals().Cases.Select(each => each.Case == "foreign" ? each with { LandedOn = "/app" } : each)]), Agents), "landed on /app");
        AssertNotPassed(FragmentsObserver.ChainRefusals(new([.. GoodRefusals().Cases.Where(each => each.Case != "expired")]), Agents), "the expired step");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(null)]
    public void O13_an_expired_step_sent_inside_ChainLifetime_or_at_no_recorded_age_does_not_pass(int? minutes)
    {
        // Mutation: removing the `name == "expired"` ChainLifetime return from FragmentsObserver.ChainRefusals passes both cases.
        ChainRefusalRecord early = new([.. GoodRefusals().Cases.Select(each => each.Case == "expired" && each.UserAgent == "firefox 133"
            ? each with { SinceMint = minutes is { } m ? TimeSpan.FromMinutes(m) : null }
            : each)]);

        AssertNotPassed(FragmentsObserver.ChainRefusals(early, Agents), "the expired step in firefox 133 was not sent past ChainLifetime");
    }

    // ---- F-T5, the asset invariant on the cell's bucket (F-37) ----

    private const string Winner = "aaaa";
    private const string Other = "bbbb";

    internal static PublishRaceRecord GoodRaces() => new(
    [
        new PublishRace("different", [new PublishRun(true, null, Winner), new PublishRun(false, 412, Other)], Winner, Winner),
        new PublishRace("same", [new PublishRun(true, null, Winner), new PublishRun(true, null, Winner)], Winner, Winner),
    ]);

    [Fact]
    public void FT5_one_completion_with_s3s_refusal_and_the_winners_bytes_passes()
    {
        // Mutation: accepting any RefusedWith in FragmentsObserver.PublishRaces passes the 500 case.
        Assert.Equal(Verdict.Passed, FragmentsObserver.PublishRaces(GoodRaces()).Verdict);
        PublishRace different = GoodRaces().Variants[0];
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different with { Runs = [different.Runs[0], different.Runs[1] with { RefusedWith = 500 }] }, GoodRaces().Variants[1]])), "not S3's 412 or 409");
    }

    [Fact]
    public void FT5_two_racing_publishes_that_both_complete_with_different_bytes_fail()
    {
        // Mutation: removing the `completed.Length == 2` return from FragmentsObserver.PublishRaces lets this reach the
        // refusal check, which reports not proved, and the Failed assertion fails.
        PublishRace different = GoodRaces().Variants[0];
        RowResult result = FragmentsObserver.PublishRaces(new([different with { Runs = [different.Runs[0], new PublishRun(true, null, Other)] }, GoodRaces().Variants[1]]));

        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, "both completed with different bytes");
    }

    [Fact]
    public void FT5_the_losers_bytes_stored_or_served_and_a_same_bytes_run_refused_fail()
    {
        // Mutation: removing the stored-bytes comparison from FragmentsObserver.PublishRaces passes the first case.
        PublishRace different = GoodRaces().Variants[0];
        PublishRace same = GoodRaces().Variants[1];
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different with { StoredSha256 = Other }, same])), "bytes other than the winner's");
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different with { ServedSha256 = Other }, same])), "the URL served bytes other than the winner's");
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different, same with { Runs = [same.Runs[0], new PublishRun(false, 412, Winner)] }])), "same-bytes publish did not complete");
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different])), "both the different-bytes and the same-bytes variant");
        AssertNotPassed(FragmentsObserver.PublishRaces(new([different with { Runs = [different.Runs[0] with { Completed = false, RefusedWith = 409 }, different.Runs[1]] }, same])), "neither racing publish completed");
    }

    // ---- the gateway's lines, as CloudWatch answers them (F-31 and N-38) ----

    internal static JsonObject Line(string task, DateTimeOffset at, JsonObject message) => new()
    {
        ["logStreamName"] = "gateway/gateway/" + task,
        ["timestamp"] = at.ToUnixTimeMilliseconds(),
        ["ingestionTime"] = at.AddSeconds(3).ToUnixTimeMilliseconds(),
        ["message"] = message.ToJsonString(),
    };

    internal static JsonObject RequestLine(Step step, string route = "bff", string? sessionRef = null) =>
        new() { ["msg"] = "request", ["trace_id"] = step.TraceId, ["route"] = route, ["session_ref"] = sessionRef, ["ts"] = step.At.ToString("O") };

    internal static async Task<GatewayCapture> CaptureAsync(params JsonObject[] lines)
    {
        JsonArray events = [.. lines.Select(line => line.DeepClone())];
        RecordedRunner runner = new(arguments => arguments is ["logs", "filter-log-events", ..]
            ? new JsonObject { ["events"] = events.DeepClone() }.ToJsonString()
            : throw new InvalidOperationException("unexpected call"));
        return await new ContinuityLog(Cell, runner).ReadAsync(SignedIn.AddHours(-1), SignedIn.AddHours(2), Ct);
    }

    // ---- Row 10, the pre-fragments refusal (F-31) ----

    private const string PreDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string PostDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static string Described(params (string Id, string Digest)[] tasks) => new JsonObject
    {
        ["tasks"] = new JsonArray([.. tasks.Select(task => (JsonNode)new JsonObject
        {
            ["taskArn"] = $"arn:aws:ecs:us-east-1:123456789012:task/spa-poc/{task.Id}",
            ["lastStatus"] = "RUNNING",
            ["desiredStatus"] = "RUNNING",
            ["containers"] = new JsonArray(new JsonObject { ["name"] = "gateway", ["imageDigest"] = task.Digest }),
        })]),
    }.ToJsonString();

    internal static async Task<RollingRefusalRecord> GoodRollingAsync()
    {
        RecordedRunner ecs = new(arguments => arguments switch
        {
            ["ecs", "list-tasks", ..] => new JsonObject { ["taskArns"] = new JsonArray($"arn:aws:ecs:us-east-1:123456789012:task/spa-poc/{TaskA}", $"arn:aws:ecs:us-east-1:123456789012:task/spa-poc/{TaskB}") }.ToJsonString(),
            ["ecs", "describe-tasks", ..] => Described((TaskA, PreDigest), (TaskB, PostDigest)),
            _ => throw new InvalidOperationException("unexpected call"),
        });
        return new RollingRefusalRecord(new ImageProvenance(Commit, PreDigest), new ImageProvenance(null, PostDigest), 42, await new FragmentsReaders(Cell, ecs).TaskImagesAsync(Ct));
    }

    internal static JsonObject[] RollingLines(bool preApplied = false) =>
    [
        Line(TaskA, SignedIn, new JsonObject { ["msg"] = "Registry document \"etag-2\" has an error: /schemaVersion: Expected \"1\" [schema]" }),
        Line(TaskA, SignedIn, new JsonObject { ["msg"] = "Registry document \"etag-2\" is rejected with 1 error(s). Revision 41 stays live." }),
        Line(TaskB, SignedIn, new JsonObject { ["msg"] = "Registry revision 42 (\"etag-2\") is live with 2 app(s)." }),
        .. preApplied ? [Line(TaskA, SignedIn.AddSeconds(30), new JsonObject { ["msg"] = "Registry revision 42 (\"etag-2\") is live with 2 app(s)." })] : Array.Empty<JsonObject>(),
    ];

    [Fact]
    public async Task Row10_a_refusal_by_version_on_the_pre_fragments_task_and_an_apply_on_the_fragments_task_pass()
    {
        // Mutation: matching the refusal by "has an error" alone, without "schemaVersion", in FragmentsObserver.RollingRefusal
        // passes the second case.
        RollingRefusalRecord record = await GoodRollingAsync();
        Assert.Equal([new TaskImage(TaskA, PreDigest), new TaskImage(TaskB, PostDigest)], record.Tasks);
        Assert.Equal(Verdict.Passed, FragmentsObserver.RollingRefusal(record, await CaptureAsync(RollingLines())).Verdict);

        JsonObject[] otherError = [Line(TaskA, SignedIn, new JsonObject { ["msg"] = "Registry document \"etag-2\" has an error: /apps/0/host: bad" }), .. RollingLines()[1..]];
        AssertNotPassed(FragmentsObserver.RollingRefusal(record, await CaptureAsync(otherError)), "no refusal by schemaVersion");
    }

    [Fact]
    public async Task Row10_a_pre_fragments_task_that_applies_version_2_fails()
    {
        // Mutation: removing the pre-fragments task's "applied" return from FragmentsObserver.RollingRefusal passes this row.
        RowResult result = FragmentsObserver.RollingRefusal(await GoodRollingAsync(), await CaptureAsync(RollingLines(preApplied: true)));

        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, $"the pre-fragments task {TaskA} applied the schemaVersion 2 document");
    }

    [Fact]
    public async Task Row10_the_registrys_wording_the_matcher_reads_is_the_registrys_own()
    {
        // Mutation: changing FragmentsObserver.StaysLive to "stays alive." fails this test, as a change to RegistrySource.cs's line would.
        string source = await File.ReadAllTextAsync(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Platform.Registry", "RegistrySource.cs"), Ct);
        string issue = await File.ReadAllTextAsync(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Platform.Registry", "RegistryIssue.cs"), Ct);

        Assert.Contains("\"Registry document {ETag} " + FragmentsObserver.HasAnError + "{Issue}\"", source, StringComparison.Ordinal);
        Assert.Contains("is rejected with {Count} error(s). Revision {Revision} " + FragmentsObserver.StaysLive + "\"", source, StringComparison.Ordinal);
        Assert.StartsWith(FragmentsObserver.IsRejected, "is rejected with {Count}", StringComparison.Ordinal);
        Assert.Contains("\"Registry revision {Revision} ({ETag}) is live with {Count} app(s).\"", source, StringComparison.Ordinal);
        Assert.Equal("Registry revision 42 (", FragmentsObserver.LivePrefix(42));
        Assert.Contains("ToString() => $\"{(Path.Length == 0 ? \"/\" : Path)}: {Message} [{Rule}]\"", issue, StringComparison.Ordinal);
        Assert.EndsWith(": ", FragmentsObserver.SchemaVersionIssue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("one image")]
    [InlineData("stays")]
    [InlineData("fragments task")]
    public async Task Row10_a_missing_revision_a_one_image_deploy_a_refusal_with_no_live_snapshot_or_an_unapplied_fragments_task_does_not_pass(string broken)
    {
        // Mutation: removing the `!byVersion || !stays` clause's `!stays`, or the fragments task's apply check, from
        // FragmentsObserver.RollingRefusal passes the "stays" or the "fragments task" case.
        RollingRefusalRecord good = await GoodRollingAsync();
        JsonObject[] lines = RollingLines();
        (RollingRefusalRecord record, JsonObject[] read, string reason) = broken switch
        {
            "revision" => (good with { Revision = null }, lines, "revision is missing"),
            "one image" => (good with { Tasks = [good.Tasks[1]] }, lines, "0 pre-fragments and 1 fragments task(s)"),
            "stays" => (good, [lines[0], lines[2]], "no refusal by schemaVersion with its snapshot staying live"),
            _ => (good, lines[..2], $"the fragments task {TaskB} did not apply revision 42"),
        };

        AssertNotPassed(FragmentsObserver.RollingRefusal(record, await CaptureAsync(read)), reason);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("digest")]
    [InlineData("fragments")]
    public async Task Row10_missing_image_provenance_or_digest_is_not_proved(string missing)
    {
        // Mutation: removing the Commit() pattern test from FragmentsObserver.RollingRefusal passes the "commit" case.
        RollingRefusalRecord good = await GoodRollingAsync();
        RollingRefusalRecord broken = missing switch
        {
            "commit" => good with { PreFragments = new ImageProvenance("main", PreDigest) },
            "digest" => good with { PreFragments = new ImageProvenance(Commit, null) },
            _ => good with { Fragments = new ImageProvenance(null, "latest") },
        };

        RowResult result = FragmentsObserver.RollingRefusal(broken, await CaptureAsync(RollingLines()));
        Assert.Equal(Verdict.NotProved, result.Verdict);
        AssertNotPassed(result, "is missing");
    }

    // ---- O3, successor revocation across tasks (N-38) ----

    private const string SessionRef = "5e5510a0f00d";

    private static Step At(string name, int seconds) => new(name, ClientTrace.Fresh().TraceId, SignedIn.AddSeconds(seconds), 200);

    internal static (IReadOnlyList<Step> Steps, SuccessorRecord Record, JsonObject[] Lines) GoodSuccessor(string outcome = "revoked")
    {
        Step logout = At("logout", 0);
        Step refresh = At("refresh", 0);
        JsonObject[] lines =
        [
            Line(TaskA, logout.At, RequestLine(logout, sessionRef: SessionRef)),
            Line(TaskB, refresh.At, RequestLine(refresh, "api", SessionRef)),
            Line(TaskB, refresh.At.AddSeconds(1), new JsonObject { ["msg"] = "Refreshed the access token", ["session_ref"] = SessionRef, ["fragment_id"] = "orders", ["ts"] = refresh.At.AddSeconds(1).ToString("O") }),
            Line(TaskA, logout.At.AddSeconds(2), new JsonObject { ["msg"] = "logout revocation", ["session_ref"] = SessionRef, ["fragment_id"] = "orders", ["outcome"] = outcome, ["ts"] = logout.At.AddSeconds(2).ToString("O") }),
        ];
        return ([logout, refresh], new SuccessorRecord(SessionRef, "orders", 400, logout.At.AddSeconds(30)), lines);
    }

    [Fact]
    public async Task O3_a_successor_refused_at_the_provider_after_the_logout_passes()
    {
        // Mutation: reading any probe status as a refusal (dropping the 2xx return) in FragmentsObserver.Successor passes the 200 case.
        (IReadOnlyList<Step> steps, SuccessorRecord record, JsonObject[] lines) = GoodSuccessor();
        GatewayCapture capture = await CaptureAsync(lines);

        Assert.Equal(Verdict.Passed, FragmentsObserver.Successor(steps, record, capture).Verdict);
        RowResult accepted = FragmentsObserver.Successor(steps, record with { ProbeStatus = 200 }, capture);
        Assert.Equal(Verdict.Failed, accepted.Verdict);
        Assert.Contains(TaskA, accepted.Reason, StringComparison.Ordinal);
        Assert.Contains(TaskB, accepted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O3_a_successor_that_refreshes_after_the_logout_fails_naming_both_tasks()
    {
        // Mutation: removing the late-refresh return from FragmentsObserver.Successor reaches the probe, which passes, and
        // the Failed assertion fails.
        (IReadOnlyList<Step> steps, SuccessorRecord record, JsonObject[] lines) = GoodSuccessor();
        JsonObject late = Line(TaskB, steps[0].At.AddSeconds(10), new JsonObject { ["msg"] = "Refreshed the access token", ["session_ref"] = SessionRef, ["fragment_id"] = "orders", ["ts"] = steps[0].At.AddSeconds(10).ToString("O") });

        RowResult result = FragmentsObserver.Successor(steps, record, await CaptureAsync([.. lines, late]));
        Assert.Equal(Verdict.Failed, result.Verdict);
        AssertNotPassed(result, $"the successor refreshed on {TaskB} after the logout on {TaskA}");
    }

    [Fact]
    public async Task O3_a_successor_obtained_before_the_logout_is_not_one_obtained_during_it()
    {
        // Mutation: taking the first Refreshed line whatever its time (dropping `>= logout.At`) in FragmentsObserver.Successor passes this row.
        (IReadOnlyList<Step> steps, SuccessorRecord record, JsonObject[] lines) = GoodSuccessor();
        JsonObject early = Line(TaskB, steps[0].At.AddSeconds(-30), new JsonObject { ["msg"] = "Refreshed the access token", ["session_ref"] = SessionRef, ["fragment_id"] = "orders", ["ts"] = steps[0].At.AddSeconds(-30).ToString("O") });

        AssertNotPassed(FragmentsObserver.Successor(steps, record, await CaptureAsync([lines[0], lines[1], early, lines[3]])), $"no successor was obtained on {TaskB} while the logout ran");
    }

    [Fact]
    public async Task O3_one_task_no_revocation_or_no_probe_is_not_proved_and_an_unrevoked_set_fails()
    {
        // Mutation: removing the `on.TaskId == other.TaskId` clause from FragmentsObserver.Successor passes the one-task case.
        (IReadOnlyList<Step> steps, SuccessorRecord record, JsonObject[] lines) = GoodSuccessor();
        JsonObject[] oneTask = [.. lines.Select(line => (JsonObject)JsonNode.Parse(line.ToJsonString().Replace(TaskB, TaskA, StringComparison.Ordinal))!)];
        AssertNotPassed(FragmentsObserver.Successor(steps, record, await CaptureAsync(oneTask)), "two different tasks");
        AssertNotPassed(FragmentsObserver.Successor(steps, record, await CaptureAsync(lines[..3])), "no logout revocation line");
        AssertNotPassed(FragmentsObserver.Successor(steps, record with { ProbeStatus = null }, await CaptureAsync(lines)), "not presented to the provider");

        (IReadOnlyList<Step> unrevokedSteps, _, JsonObject[] unrevoked) = GoodSuccessor("gate_timeout");
        RowResult left = FragmentsObserver.Successor(unrevokedSteps, record, await CaptureAsync(unrevoked));
        Assert.Equal(Verdict.Failed, left.Verdict);
        AssertNotPassed(left, "outcome gate_timeout");
    }

    [Fact]
    public async Task O3_a_refresh_line_is_the_fragments_by_its_request_lines_fragment_id_and_another_sets_is_no_successor()
    {
        // Mutation: reading a Refreshed line with no fragment_id as the fragment's (FragmentsObserver.FragmentOf's
        // `is not { } id ||` form, before the review) passes the shell's refresh as the successor.
        (IReadOnlyList<Step> steps, SuccessorRecord record, JsonObject[] lines) = GoodSuccessor();
        Step refresh = steps[1];
        DateTimeOffset refreshedAt = refresh.At.AddSeconds(1);
        // As contracts/telemetry.md (#986) writes them: fragment_id on the request line only, trace_id on both.
        JsonObject refreshed = Line(TaskB, refreshedAt, new JsonObject { ["msg"] = "Refreshed the access token", ["session_ref"] = SessionRef, ["trace_id"] = refresh.TraceId, ["ts"] = refreshedAt.ToString("O") });
        JsonObject fragmentRequest = RequestLine(refresh, "api", SessionRef);
        fragmentRequest["fragment_id"] = "orders";

        RowResult fragments = FragmentsObserver.Successor(steps, record, await CaptureAsync(lines[0], Line(TaskB, refresh.At, fragmentRequest), refreshed, lines[3]));
        Assert.True(fragments.Verdict == Verdict.Passed, fragments.Message);

        RowResult shell = FragmentsObserver.Successor(steps, record, await CaptureAsync(lines[0], lines[1], refreshed, lines[3]));
        AssertNotPassed(shell, $"no successor was obtained on {TaskB} while the logout ran");
    }
}
