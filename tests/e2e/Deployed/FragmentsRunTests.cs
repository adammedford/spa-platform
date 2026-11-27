using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// What the fragments class does around its observer (#1005), with no AWS: every row's satisfying
/// recording passing through the run with its evidence, the rerun budget, the refusal of an evidence
/// path git does not ignore, N-16's canaries, the local stack's skip, and the method names
/// contracts/test-ids.md gives the id-bearing rows. Each test names in its body the mutation that turns it red.
/// </summary>
public sealed class FragmentsRunTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>N-16's kind of canary: a data-protection payload, as a session cookie, ticket or continuation is.</summary>
    private const string Canary = "CfDJ8canary-continuation-0123456789";

    private readonly string directory = Directory.CreateTempSubdirectory("spa-e2e-fragments-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Step Control() => new("control", ClientTrace.Fresh().TraceId, Now, 200) { Expect = 200, Route = "health" };

    /// <summary>git check-ignore answers <paramref name="ignored"/>; filter-log-events answers the control's line and <paramref name="lines"/>.</summary>
    private static RecordedRunner Runner(bool ignored, Step control, params JsonObject[] lines) => RecordedRunner.WithExits(command => command switch
    {
        { Program: "git", Arguments: [.., "check-ignore", "-q", "--", _] } => new CommandResult(ignored ? 0 : 1, ""),
        { Program: "aws", Arguments: ["logs", "filter-log-events", ..] } => new CommandResult(0, new JsonObject
        {
            ["events"] = new JsonArray([FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, control.At, FragmentsObserverTests.RequestLine(control, "health")), .. lines.Select(line => line.DeepClone())]),
        }.ToJsonString()),
        _ => throw new InvalidOperationException($"unexpected {command.Program} {string.Join(' ', command.Arguments)}"),
    });

    private FragmentsRun Run(RecordedRunner runner) =>
        new(new FragmentsEvidence(directory, runner), new ContinuityLog(FragmentsObserverTests.Cell, runner)) { Delivery = TimeSpan.Zero };

    private FragmentsRowEvidence Written(string row) =>
        JsonSerializer.Deserialize<FragmentsRowEvidence>(File.ReadAllText(Path.Combine(directory, row + ".json")))!;

    private static FragmentsContext Context(int n = 2) => new(n, FragmentsObserverTests.Agents, [FragmentsObserverTests.TaskB]);

    private static Driven<T> Drive<T>(T record, int n = 2) => new(Context(n), [], record);

    /// <summary>Each row with its satisfying recording, judged through the run as its Fact judges it.</summary>
    public static TheoryData<string> Rows => [.. FragmentsBounds.ByRow.Keys];

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Each_rows_satisfying_recording_passes_through_the_run_and_its_evidence_records_N_agents_and_tasks(string row)
    {
        // Mutation: dropping the located task ids from FragmentsRun.Recorded leaves TaskIds without the control's task and
        // fails every row; writing `last` from the earlier file instead of this run's attempt drops N and the agents.
        Step control = Control();
        (IReadOnlyList<Step> o3Steps, SuccessorRecord o3, JsonObject[] o3Lines) = FragmentsObserverTests.GoodSuccessor();
        RollingRefusalRecord rolling = await FragmentsObserverTests.GoodRollingAsync();
        JsonObject[] lines = row switch
        {
            FragmentsBounds.O3 => o3Lines,
            FragmentsBounds.Row10 => FragmentsObserverTests.RollingLines(),
            _ => [],
        };
        RecordedRunner runner = Runner(true, control, lines);

        RowResult result = row switch
        {
            FragmentsBounds.O1 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodRedirects())), (d, _) => FragmentsObserver.Redirects(d.Record), Ct),
            FragmentsBounds.O2 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodContention())), (d, _) => FragmentsObserver.Contention(d.Record), Ct),
            FragmentsBounds.O3 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(new Driven<SuccessorRecord>(Context(), o3Steps, o3)), (d, c) => FragmentsObserver.Successor(d.Steps, d.Record, c), Ct),
            FragmentsBounds.O4 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(new RecordSize(new ItemSize(369, 300),
                new CapacitySample("sign-ins", Now.AddMinutes(-3), Now.AddMinutes(-1), 2, [new CapacityPeriod(Minute(Now.AddMinutes(-4)), 0), new CapacityPeriod(Minute(Now.AddMinutes(-3)), 1), new CapacityPeriod(Minute(Now.AddMinutes(-2)), 1), new CapacityPeriod(Minute(Now.AddMinutes(-1)), 1), new CapacityPeriod(Minute(Now), 0)], [], [])))),
                (d, _) => FragmentsObserver.Size(d.Record), Ct),
            FragmentsBounds.O5 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodMaps())), (d, _) => FragmentsObserver.ImportMap(d.Record, d.Context.UserAgents), Ct),
            FragmentsBounds.O8 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodFallback())), (d, _) => FragmentsObserver.Fallback(d.Record), Ct),
            FragmentsBounds.O9 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodHeaders())), (d, _) => FragmentsObserver.HeaderLimits(d.Record, d.Context.UserAgents), Ct),
            FragmentsBounds.O12 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodImports())), (d, _) => FragmentsObserver.EntryImports(d.Record), Ct),
            FragmentsBounds.O13Chain => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodChain())), (d, _) => FragmentsObserver.Chain(d.Record, d.Context), Ct),
            FragmentsBounds.O13Refusal => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodRefusals())), (d, _) => FragmentsObserver.ChainRefusals(d.Record, d.Context.UserAgents), Ct),
            FragmentsBounds.FT5 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(Drive(FragmentsObserverTests.GoodRaces())), (d, _) => FragmentsObserver.PublishRaces(d.Record), Ct),
            FragmentsBounds.Row10 => await Run(runner).RowAsync(row, control, (_, _) => Task.FromResult(new Driven<RollingRefusalRecord>(Context() with { TaskIds = [.. rolling.Tasks.Select(task => task.TaskId)] }, [], rolling)), (d, c) => FragmentsObserver.RollingRefusal(d.Record, c), Ct),
            _ => throw new InvalidOperationException($"{row} has no recording here"),
        };

        Assert.True(result.Verdict == Verdict.Passed, result.Message);
        FragmentsRowEvidence evidence = Written(row);
        Assert.Equal(Verdict.Passed, evidence.Verdict);
        Assert.Equal(1, evidence.Attempts);
        Assert.Equal(2, evidence.N);
        Assert.Equal(FragmentsObserverTests.Agents, evidence.UserAgents);
        Assert.Contains(FragmentsObserverTests.TaskA, evidence.TaskIds);
        Assert.NotEqual(JsonValueKind.Undefined, Assert.Single(evidence.Runs).Record.ValueKind);
    }

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task F31s_read_keeps_the_lines_its_rolling_deploy_wrote_long_after_the_control()
    {
        // Mutation: dropping `driven.Until` from FragmentsRun.WaitAndReadAsync's cut (the cut before the review) reads only
        // to the control's delivery, loses every refusal and apply line, and fails this test.
        Step control = Control();
        DateTimeOffset deploy = Now.AddMinutes(10);
        JsonObject[] lines =
        [
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, deploy, new JsonObject { ["msg"] = "Registry document \"etag-2\" has an error: /schemaVersion: Expected \"1\" [schema]" }),
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskA, deploy, new JsonObject { ["msg"] = "Registry document \"etag-2\" is rejected with 1 error(s). Revision 41 stays live." }),
            FragmentsObserverTests.Line(FragmentsObserverTests.TaskB, deploy, new JsonObject { ["msg"] = "Registry revision 42 (\"etag-2\") is live with 2 app(s)." }),
        ];
        RollingRefusalRecord rolling = await FragmentsObserverTests.GoodRollingAsync();

        RowResult result = await Run(Runner(true, control, lines)).RowAsync(FragmentsBounds.Row10, control,
            (_, _) => Task.FromResult(new Driven<RollingRefusalRecord>(Context() with { TaskIds = [.. rolling.Tasks.Select(task => task.TaskId)] }, [], rolling) { Until = deploy.AddMinutes(1) }),
            (d, c) => FragmentsObserver.RollingRefusal(d.Record, c),
            Ct);

        Assert.True(result.Verdict == Verdict.Passed, result.Message);
    }

    [Fact]
    public async Task A_rerun_reads_the_earlier_evidence_and_spends_only_the_attempts_left()
    {
        // Mutation: making FragmentsEvidence.Left return the full bound whatever the earlier file holds makes 10 attempts
        // here and fails this test.
        Step control = Control();
        FragmentsRowEvidence earlier = new(FragmentsBounds.O3, 7, 300, Verdict.NotProved, "the race was not observed", [], 2, [], [FragmentsObserverTests.TaskA], []);
        await File.WriteAllTextAsync(Path.Combine(directory, FragmentsBounds.O3 + ".json"), JsonSerializer.Serialize(earlier), Ct);
        int made = 0;

        RowResult result = await Run(Runner(true, control)).RowAsync(FragmentsBounds.O3, control,
            (_, _) =>
            {
                made++;
                return Task.FromResult(new Driven<SuccessorRecord>(Context(), [], new SuccessorRecord("", "")));
            },
            (d, c) => FragmentsObserver.Successor(d.Steps, d.Record, c),
            Ct);

        Assert.Equal(3, made);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        FragmentsRowEvidence written = Written(FragmentsBounds.O3);
        Assert.Equal(FragmentsBounds.Of(FragmentsBounds.O3).Attempts, written.Attempts);
        Assert.True(written.WallSeconds >= 300);
        Assert.Equal(3, written.Runs.Count);
    }

    [Fact]
    public async Task A_rerun_with_its_wall_time_spent_drives_nothing_and_reports_not_proved()
    {
        // Mutation: dropping the `budget.Spent` return from FragmentsRun.RowAsync drives once here and fails this test.
        Step control = Control();
        FragmentsRowEvidence earlier = new(FragmentsBounds.O8, 0, FragmentsBounds.Of(FragmentsBounds.O8).Wall.TotalSeconds, Verdict.NotProved, "earlier", [], 2, [], [], []);
        await File.WriteAllTextAsync(Path.Combine(directory, FragmentsBounds.O8 + ".json"), JsonSerializer.Serialize(earlier), Ct);
        bool driven = false;

        RowResult result = await Run(Runner(true, control)).RowAsync(FragmentsBounds.O8, control,
            (_, _) => { driven = true; return Task.FromResult(Drive(FragmentsObserverTests.GoodFallback())); },
            (d, _) => FragmentsObserver.Fallback(d.Record),
            Ct);

        Assert.False(driven);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("fresh " + FragmentsEvidence.Variable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_stops_attempting_once_the_wall_time_left_is_spent()
    {
        // Mutation: dropping `wall.Elapsed < budget.Wall` from FragmentsRun.RowAsync's loop makes the 9 attempts left here.
        Step control = Control();
        RowBound bound = FragmentsBounds.Of(FragmentsBounds.O3);
        FragmentsRowEvidence earlier = new(FragmentsBounds.O3, 1, (bound.Wall - TimeSpan.FromSeconds(1)).TotalSeconds, Verdict.NotProved, "earlier", [], 2, [], [], []);
        await File.WriteAllTextAsync(Path.Combine(directory, FragmentsBounds.O3 + ".json"), JsonSerializer.Serialize(earlier), Ct);
        int made = 0;

        await Run(Runner(true, control)).RowAsync(FragmentsBounds.O3, control,
            async (_, ct) =>
            {
                made++;
                await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
                return new Driven<SuccessorRecord>(Context(), [], new SuccessorRecord("", ""));
            },
            (d, c) => FragmentsObserver.Successor(d.Steps, d.Record, c),
            Ct);

        Assert.Equal(1, made);
    }

    [Fact]
    public void O8s_bound_allows_for_the_managed_login_hour_and_every_row_has_one()
    {
        // Mutation: setting O8's wall bound to 60 minutes (the hour alone) in FragmentsBounds.ByRow fails this test.
        Assert.True(FragmentsBounds.Of(FragmentsBounds.O8).Wall > FragmentsBounds.ManagedLoginHour + TimeSpan.FromMinutes(10));
        Assert.Equal(12, FragmentsBounds.ByRow.Count);
        Assert.All(FragmentsBounds.ByRow.Values, bound => Assert.True(bound.Attempts > 0 && bound.Wall > TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => FragmentsBounds.Of("row3"));
    }

    [Fact]
    public async Task An_evidence_path_git_does_not_ignore_is_refused_before_anything_is_driven_or_written()
    {
        // Mutation: dropping the ExitCode check from FragmentsEvidence.RefuseUnlessIgnoredAsync drives and writes here.
        Step control = Control();
        RecordedRunner runner = Runner(false, control);
        bool driven = false;

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(runner).RowAsync(FragmentsBounds.O5, control,
            (_, _) => { driven = true; return Task.FromResult(Drive(FragmentsObserverTests.GoodMaps())); },
            (d, _) => FragmentsObserver.ImportMap(d.Record, d.Context.UserAgents), Ct));

        Assert.Contains("not ignored by git", refused.Message, StringComparison.Ordinal);
        Assert.False(driven);
        Assert.Empty(Directory.GetFiles(directory));
        Command check = Assert.Single(runner.Commands);
        Assert.Equal(["-C", directory, "check-ignore", "-q", "--", Path.Combine(directory, FragmentsBounds.O5 + ".json")], check.Arguments);
    }

    [Fact]
    public async Task A_row_the_driver_cannot_yet_drive_reports_not_proved_naming_the_part_and_records_its_context()
    {
        // Mutation: judging a Driven whose Missing is set (dropping the Missing branch in FragmentsRun.RowAsync) judges an
        // empty recording, which the observer reports otherwise, and fails this test.
        Step control = Control();
        RowResult result = await Run(Runner(true, control)).RowAsync(FragmentsBounds.O12, control,
            (_, _) => Task.FromResult(Drive(new EntryImportRecord([]), n: 3) with { Missing = "import a fragment's entry" }),
            (d, _) => FragmentsObserver.EntryImports(d.Record),
            Ct);

        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.StartsWith("not proved: the driver cannot yet import a fragment's entry", result.Message, StringComparison.Ordinal);
        Assert.Equal(3, Written(FragmentsBounds.O12).N);
    }

    [Fact]
    public async Task No_cookie_token_code_state_or_continuation_reaches_a_message_or_an_evidence_file()
    {
        // Mutation: removing the Unsafe check from FragmentsEvidence.WriteAsync writes the canary into the file; removing
        // FragmentsRun.Printable prints it in the failed verdict's message.
        Step control = Control();
        RedirectRecord leaky = new([.. FragmentsObserverTests.GoodRedirects().Engines.Select(engine => engine.Engine != "chromium" ? engine : engine with
        {
            Runs = [.. engine.Runs.Select(run => run.N != 0 ? run : run with { Uninterrupted = [[new Redirect(302, $"/signin-oidc?code={Canary}&state={Canary}")]] })],
        })]);

        await Assert.ThrowsAsync<UnsafeEvidenceException>(() => Run(Runner(true, control)).RowAsync(FragmentsBounds.O1, control,
            (_, _) => Task.FromResult(Drive(leaky)),
            (d, _) => RowResult.Failed("echoed " + d.Record.Engines[0].Runs[0].Uninterrupted![0][0].Path, []),
            Ct));

        string file = await File.ReadAllTextAsync(Path.Combine(directory, FragmentsBounds.O1 + ".json"), Ct);
        Assert.DoesNotContain(Canary, file, StringComparison.Ordinal);
        FragmentsRowEvidence written = Written(FragmentsBounds.O1);
        Assert.Equal(1, written.Attempts);
        Assert.Empty(written.Runs);
        Assert.Contains("held a data-protection payload", written.Reason, StringComparison.Ordinal);

        RowResult printed = FragmentsRun.Printable(RowResult.Failed("echoed /x", [$"__Host-spa-session={Canary}"]));
        Assert.DoesNotContain(Canary, printed.Message, StringComparison.Ordinal);
        Assert.Equal(Verdict.Failed, printed.Verdict);
    }

    [Theory]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln", "a JWT-shaped value")]
    [InlineData("CfDJ8AAAA", "a data-protection payload")]
    [InlineData("/signin-oidc?code=x", "a query parameter")]
    [InlineData("/bff/chain?c=x", "a query parameter")]
    [InlineData("/signin-oidc?x=1\\u0026state=x", "a query parameter")]
    [InlineData("__Host-spa-csrf=abc", "a __Host- cookie")]
    [InlineData("__Host-spa-corr.Xk3vQ9=N", "a __Host- cookie")]
    public void The_guard_names_each_credential_shape_and_passes_a_clean_record(string text, string kind)
    {
        // Mutation: removing any one arm of FragmentsEvidence.Unsafe passes its case (M44 and M54 to M57 of the Task commit);
        // taking the dot out of HostCookie's name class passes the correlation cookie's case.
        Assert.StartsWith(kind, FragmentsEvidence.Unsafe("{\"path\":\"" + text + "\"}"), StringComparison.Ordinal);
        Assert.Null(FragmentsEvidence.Unsafe(JsonSerializer.Serialize(FragmentsObserverTests.GoodRedirects())));
    }

    // ---- the class: its skip on the local stack, and its names ----

    private static MethodInfo[] RowFacts() =>
        [.. typeof(FragmentsTests).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => method.GetCustomAttribute<FactAttribute>() is not null)];

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    public async Task Each_row_test_reports_skipped_naming_the_switch_on_the_local_stack_before_any_aws_call(string? stack)
    {
        // Mutation: moving FragmentsStack.SkipUnlessDeployed after the stack's start in FragmentsDriver.StartAsync counts a start.
        FragmentsStack.Environment.Value = name => name == StackSwitch.Variable ? stack : null;
        int started = 0;
        RecordedRunner runner = new((_, _) => "{}");
        FragmentsStack.Deployed.Value = new DeployedPath(
            () =>
            {
                started++;
                // Never dereferenced under the skip: the driver is built only after it.
                return Task.FromResult<DeployedStack>(null!);
            },
            runner);

        MethodInfo[] rows = RowFacts();
        Assert.Equal(12, rows.Length);
        foreach (MethodInfo row in rows)
        {
            // Caught by hand: Assert.Throws would hand a skip on to the runner, and this test would report skipped.
            Exception? skipped = null;
            try
            {
                await (Task)row.Invoke(new FragmentsTests(), null)!;
            }
            catch (Exception thrown)
            {
                skipped = thrown;
            }

            Assert.Equal("SkipException", skipped?.GetType().Name);
            Assert.Contains(StackSwitch.Variable, skipped!.Message, StringComparison.Ordinal);
            Assert.True(started == 0, $"{row.Name} started the deployed stack before its skip");
            Assert.True(runner.Calls.Count == 0, $"{row.Name} ran {string.Join(", ", runner.Calls.Select(call => call.Program))} before its skip");
        }
    }

    /// <summary>
    /// The methods contracts/test-ids.md names for the class's rows, as landing B's register numbers them
    /// (F-31, F-37, F-39 to F-42, N-38 and N-46; #989's decision, d71e3d3).
    /// </summary>
    private static readonly IReadOnlyList<string> RegisteredNames =
    [
        "G9_a_pre_fragments_build_refuses_a_schemaVersion_2_document_across_a_rolling_deploy",
        "G9_O10_F_T5_two_racing_publishes_on_the_cell_s_bucket_complete_once",
        "G9_O5_the_hashed_import_map_cases_in_each_browser",
        "G9_O8_the_fallback_is_page_less_inside_the_managed_login_hour_and_prompts_after_it",
        "G9_O12_the_entry_module_imports_and_its_relative_chunk_resolves",
        "G9_O13_the_chain_completes_and_each_step_s_fetch_metadata_is_recorded",
        "G9_O3_a_successor_obtained_during_logout_is_refused",
        "G9_O13_a_replayed_foreign_or_expired_step_lands_on_root",
    ];

    [Fact]
    public void The_id_bearing_row_tests_carry_the_registers_names_and_the_record_rows_their_obligation()
    {
        // Mutation: renaming G9_O3_a_successor_obtained_during_logout_is_refused in FragmentsTests fails this test.
        string[] names = [.. RowFacts().Select(method => method.Name)];

        Assert.All(RegisteredNames, name => Assert.Contains(name, names));
        string[] records = [.. names.Except(RegisteredNames)];
        Assert.Equal(["G9_O1_", "G9_O2_", "G9_O4_", "G9_O9_"], records.Select(name => name[..6]).Order(StringComparer.Ordinal));
        Assert.All(RowFacts(), method => Assert.True(method.ReturnType == typeof(Task) && method.GetParameters().Length == 0, $"{method.Name} is not a plain Task Fact"));
    }

    /// <summary>
    /// Two fragments rows as landing B's contracts/test-ids.md registers them (#989's decision, d71e3d3): this branch's
    /// register holds none, so the check below is held to this text as well as to the real file.
    /// </summary>
    private const string PlantedRegister = """
        | ID | Attempt | Expect | Tier | Recorded by |
        | --- | --- | --- | --- | --- |
        | N-38 | Revoke a fragment's set at logout while a concurrent refresh obtains its successor, O3 (#961): `tests/e2e/Deployed/FragmentsTests.cs`: `FragmentsTests.G9_O3_a_successor_obtained_during_logout_is_refused` | The successor is refused | D | change request #989 (fragments rows) |
        | F-37 | The hashed import map's cases, O5 (#961): `tests/e2e/Deployed/FragmentsTests.cs`: `FragmentsTests.G9_O5_the_hashed_import_map_cases_in_each_browser` | Each case holds in each sitting browser | D | change request #989 (fragments rows) |
        """;

    [Fact]
    public async Task Every_FragmentsTests_method_the_register_names_is_a_row_fact_of_the_class()
    {
        // On this branch's base contracts/test-ids.md names no FragmentsTests method, so the real file's check holds
        // nothing until landing B (#989); the planted rows hold it here.
        string register = await File.ReadAllTextAsync(Path.Combine(Gateway.TestHarness.FixtureFiles.RepositoryRoot, "contracts", "test-ids.md"), Ct);

        AssertEveryNamedMethodIsARowFact(register);
        // Mutation: renaming G9_O5_the_hashed_import_map_cases_in_each_browser in FragmentsTests fails this line.
        Assert.Equal(2, AssertEveryNamedMethodIsARowFact(PlantedRegister));
    }

    [Theory]
    [InlineData("G9_O5_the_hashed_import_map_cases_in_each_browser`", "G9_O5_missing`")]
    [InlineData("G9_O3_a_successor_obtained_during_logout_is_refused`", "G9_O3_a_successor_refused`")]
    public void A_register_naming_a_method_the_class_lacks_fails_naming_the_method(string before, string after)
    {
        // Mutation: returning from AssertEveryNamedMethodIsARowFact before its assertions passes this test.
        string register = PlantedRegister.Replace(before, after, StringComparison.Ordinal);
        Assert.NotEqual(PlantedRegister, register);

        Xunit.Sdk.XunitException thrown = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEveryNamedMethodIsARowFact(register));
        Assert.Contains(after.TrimEnd('`'), thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Asserts each method the register names as <c>FragmentsTests.&lt;method&gt;</c> is a row Fact and in <see cref="RegisteredNames"/>; returns how many it names.</summary>
    private static int AssertEveryNamedMethodIsARowFact(string register)
    {
        string[] named = [.. System.Text.RegularExpressions.Regex.Matches(register, @"`FragmentsTests\.(?<method>[A-Za-z_][A-Za-z0-9_]*)`", System.Text.RegularExpressions.RegexOptions.None, Gateway.TestHarness.RegexTimeout.Span)
            .Select(match => match.Groups["method"].Value).Distinct(StringComparer.Ordinal)];
        string[] facts = [.. RowFacts().Select(method => method.Name)];

        Assert.All(named, name => Assert.True(facts.Contains(name, StringComparer.Ordinal), $"the register names FragmentsTests.{name}, which is no row Fact of the class"));
        Assert.All(named, name => Assert.True(RegisteredNames.Contains(name, StringComparer.Ordinal), $"the register names FragmentsTests.{name}, which RegisteredNames does not copy"));
        return named.Length;
    }
}
