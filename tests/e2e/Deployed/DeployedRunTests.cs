using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.E2ETests.Negative;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed negative subset run on the stack <see cref="DeployedStack"/> builds (#211), with no AWS:
/// the hermetic cell behind a loopback TLS listener stands in for the ALB at the tunnel's local port, and
/// a recorded runner answers the AWS CLI's <c>logs filter-log-events</c> with the cell's own captured
/// lines, so each refusal's rule id is read as a sitting reads it from CloudWatch (<see cref="CellRuleLog"/>).
/// Each row is the negative test itself, its <see cref="Stacks.StartAsync"/> answered with such a stack
/// (<see cref="Stacks.Instead"/>). The run on the cell itself is #63's G4 row.
/// </summary>
public sealed class DeployedRunTests
{
    /// <summary>
    /// The D rows' tests that cannot run over the stand-in, each with the reason: what they need is
    /// Cognito's own or the real two-task continuity driver, which a sitting alone shows (#657).
    /// The stand-in has one host and a test provider that approves at once at its own paths.
    /// </summary>
    private static readonly Dictionary<string, string> OutsideStandIn = new(StringComparer.Ordinal)
    {
        ["SessionTests.N01_app1s_session_cookie_presented_to_app2_gets_401_on_the_api_and_a_fresh_challenge_on_navigation"] =
            "finds the challenge by Cognito's /oauth2/authorize endpoint (Cell.AuthorizeEndpoint), which the test provider does not serve",
        ["CsrfTests.N06_an_unsafe_call_without_the_sessions_own_token_is_403_csrf_failed_each_way"] =
            "stops at the provider's login form after a logout (StopAtProviderLoginAsync), which the test provider never shows",
        ["HeaderTests.N23_no_bff_response_the_browser_received_holds_a_token_or_a_token_shaped_string"] =
            "stops at the provider's login form after a logout (StopAtProviderLoginAsync), which the test provider never shows",
    };

    [Fact]
    public async Task N26_runs_in_chromium_over_the_tunnels_local_port_with_its_rule_read_from_the_gateways_cloudwatch_lines()
    {
        await using Cells cells = new();
        Stacks.Instead.Value = cells.StartAsync;

        await new CookiePlantingTests().N26_login_csrf_a_siblings_login_state_cookies_are_never_set_so_the_planted_callback_is_rejected();

        Run run = Assert.Single(cells.Runs);
        // Every app request went to the tunnel's port: the callback the victim's browser was refused at
        // and the attacker's own, which signed in.
        Assert.Equal(2, run.Tunnel.Exchanges.Count(exchange => exchange.Url.Host == "app1." + run.Stack.ParentDomain && exchange.Url.AbsolutePath == "/signin-oidc"));
        // And the rule came from the CLI's log read, not from the captured log in process.
        Assert.Contains(run.Runner.Calls, call => call.Program == "aws" && call.Arguments is ["logs", "filter-log-events", ..]);
        Assert.DoesNotContain(run.Runner.Calls, call => call.Arguments is not ["logs", ..]);
    }

    [Fact]
    public async Task A_refusal_the_cells_log_does_not_carry_fails_the_row()
    {
        // The failing input for the rows here: a CloudWatch with no auth.callback line, so a row that
        // passed without reading the log would pass here too.
        await using Cells cells = new(line => !(line.TryGetProperty("rule", out JsonElement rule) && rule.GetString() == "auth.callback"));
        Stacks.Instead.Value = cells.StartAsync;

        Exception failed = await Assert.ThrowsAnyAsync<Exception>(
            () => new CookiePlantingTests().N26_login_csrf_a_siblings_login_state_cookies_are_never_set_so_the_planted_callback_is_rejected());
        Assert.Contains("auth.callback", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task N08_over_the_stand_in_starts_no_login_at_its_provider()
    {
        // The row's own "neither started a login" compares the navigations with the cell's Cognito
        // authorize endpoint, which the stand-in's browser never reaches, so over the stand-in that clause
        // cannot fail. The stand-in's provider is the harness's test provider, whose record can.
        await using Cells cells = new();
        Stacks.Instead.Value = cells.StartAsync;

        await new HostTests().N08_an_unknown_host_and_an_app_host_on_the_wrong_port_are_404();

        Run run = Assert.Single(cells.Runs);
        Assert.Empty(run.Harness.Idp.Authorizations);
    }

    /// <summary>Every negative test of a D row, by class and method, but those <see cref="OutsideStandIn"/> names.</summary>
    public static TheoryData<string> DeployedRowTests => [.. StandInRowTests(TestsPage(), RegisterPage())];

    private static string[] StandInRowTests(string page, string register) =>
        [.. RowTests(page, register).Keys.Where(test => !StandInExclusions(register).ContainsKey(test)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(DeployedRowTests))]
    public async Task A_d_row_runs_on_the_deployed_stack_over_the_tunnels_local_port(string test)
    {
        MethodInfo method = RowTests()[test];
        object?[][] cases = CasesOf(method);
        foreach (object?[] arguments in cases)
        {
            await using Cells cells = new();
            Stacks.Instead.Value = cells.StartAsync;

            await (Task)method.Invoke(Activator.CreateInstance(method.DeclaringType!), arguments)!;

            // The row started its stack through the switch, and so on the deployed stack: never a local one of its own.
            Assert.NotEmpty(cells.Runs);
        }
    }

    [Fact]
    public void Every_d_row_has_a_test_and_each_left_out_here_is_a_d_rows_test_named_with_its_reason()
    {
        Dictionary<string, MethodInfo> tests = RowTests();

        Assert.Contains("CookiePlantingTests.N26_login_csrf_a_siblings_login_state_cookies_are_never_set_so_the_planted_callback_is_rejected", DeployedRowTests.Select(row => row.Data));
        AssertEveryDRowHasATest(TestsPage(), RegisterPage(), tests.Keys);
        Assert.All(StandInExclusions(RegisterPage()), left => Assert.True(tests.ContainsKey(left.Key) && left.Value.Length > 0, $"{left.Key} is not a D row's test."));
    }

    private const string ContinuityRegister = """
        ## Negative security (N)

        | ID | Attempt | Expect | Tier | Recorded by |
        | --- | --- | --- | --- | --- |
        | N-32 | Replay; `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other` | 401 | D | #539 |
        """;

    [Fact]
    public void A_registered_continuity_fact_satisfies_both_readers_and_is_excluded_from_the_single_host_stand_in()
    {
        const string name = "ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other";
        Dictionary<string, MethodInfo> tests = RowTests(ExportPageWithNoDRow, ContinuityRegister);
        Assert.Equal(name, Assert.Single(tests).Key);
        AssertEveryDRowHasATest(ExportPageWithNoDRow, ContinuityRegister, tests.Keys);
        DeployedSubsetTests.AssertEveryDRowHasATest(ExportPageWithNoDRow, ContinuityRegister, []);
        Assert.Contains("two-task", StandInExclusions(ContinuityRegister)[name], StringComparison.Ordinal);
        Assert.DoesNotContain(name, StandInRowTests(ExportPageWithNoDRow, ContinuityRegister));
        const string emptyRegister = "## Negative security (N)\n| ID | Attempt | Expect | Tier | Recorded by |\n";
        Assert.Equal(StandInRowTests(TestsPage(), emptyRegister), StandInRowTests(TestsPage(), ContinuityRegister));
        Assert.All(StandInExclusions(ContinuityRegister).Except(OutsideStandIn), left => Assert.True(tests.ContainsKey(left.Key) && left.Value.Length > 0, $"{left.Key} is not a D row's test."));
    }

    [Theory]
    [InlineData("Row9_a_session_ended_on_one_task_is_refused_by_the_other", "Row9_missing")]
    [InlineData("ContinuityTests.cs", "MissingTests.cs")]
    public void A_missing_registered_continuity_fact_fails_both_readers_naming_the_id(string before, string after)
    {
        string register = ContinuityRegister.Replace(before, after, StringComparison.Ordinal);
        Dictionary<string, MethodInfo> tests = RowTests(ExportPageWithNoDRow, register);
        Assert.Empty(tests);
        Xunit.Sdk.XunitException run = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEveryDRowHasATest(ExportPageWithNoDRow, register, tests.Keys));
        Xunit.Sdk.XunitException subset = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DeployedSubsetTests.AssertEveryDRowHasATest(ExportPageWithNoDRow, register, []));
        Assert.Contains("N-32", run.Message, StringComparison.Ordinal);
        Assert.Contains("N-32", subset.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_register_row_naming_two_continuity_facts_fails_the_reader_naming_the_row()
    {
        // Resolving the first and dropping the second would leave the second unproved and unread; the Python
        // reader refuses the same row (tools/bench/e2e_evidence.py), so neither credits one of two.
        string register = ContinuityRegister.Replace(
            "`ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other`",
            "`ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other`, `ContinuityTests.Row8_fifty_calls_on_a_due_token_across_two_tasks_refresh_once`",
            StringComparison.Ordinal);
        Assert.NotEqual(ContinuityRegister, register);

        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DeployedSubsetTests.ContinuityRowTests(register));
        Assert.Contains("N-32 names more than one continuity Fact", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_register_row_naming_one_continuity_fact_twice_names_one()
    {
        // A method named twice in one row is still one method, so the row resolves rather than being refused
        // as naming two; the Python reader's twin is test_a_row_naming_one_continuity_fact_twice_names_one.
        const string fact = "`ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other`";
        string register = ContinuityRegister.Replace(fact, $"{fact}, {fact}", StringComparison.Ordinal);
        Assert.NotEqual(ContinuityRegister, register);

        KeyValuePair<string, MethodInfo> row = Assert.Single(DeployedSubsetTests.ContinuityRowTests(register));
        Assert.Equal("N-32", row.Key);
        Assert.Equal("Row9_a_session_ended_on_one_task_is_refused_by_the_other", row.Value.Name);
    }

    [Fact]
    public void A_registered_continuity_method_that_is_not_a_plain_task_fact_is_not_the_rows_test()
    {
        // Each method below breaks one clause of the filter and meets the rest, so deleting any one
        // clause lets its method through and fails the second assertion (CODING_STANDARDS.md, a check that
        // is vacuous today). They are emitted, since a public Theory without data, or a Fact with a
        // parameter, would be a test of this suite that xUnit's analyzers refuse and its runner would run.
        (string Method, Type Returns, Type[] Parameters, Type? Attribute)[] methods =
        [
            ("Row_fact_task_no_parameters", typeof(Task), [], typeof(FactAttribute)),
            ("Row_theory", typeof(Task), [], typeof(TheoryAttribute)),
            ("Row_no_attribute", typeof(Task), [], null),
            ("Row_value_task", typeof(ValueTask), [], typeof(FactAttribute)),
            ("Row_void", typeof(void), [], typeof(FactAttribute)),
            ("Row_with_a_parameter", typeof(Task), [typeof(int)], typeof(FactAttribute)),
        ];
        TypeBuilder builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ContinuityFilterFixtures"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("ContinuityFilterFixtures").DefineType("ContinuityTests", TypeAttributes.Public | TypeAttributes.Sealed);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        foreach ((string method, Type returns, Type[] parameters, Type? attribute) in methods)
        {
            MethodBuilder defined = builder.DefineMethod(method, MethodAttributes.Public | MethodAttributes.HideBySig, returns, parameters);
            ILGenerator body = defined.GetILGenerator();
            if (returns != typeof(void))
            {
                body.Emit(OpCodes.Ldnull);
            }

            body.Emit(OpCodes.Ret);
            if (attribute is not null)
            {
                ConstructorInfo constructor = attribute.GetConstructors().Single();
                object?[] defaults = [.. constructor.GetParameters().Select(parameter => parameter.DefaultValue is DBNull ? null : parameter.DefaultValue)];
                defined.SetCustomAttribute(new CustomAttributeBuilder(constructor, defaults));
            }
        }

        string register = "## Negative security (N)\n\n| ID | Attempt | Expect | Tier | Recorded by |\n| --- | --- | --- | --- | --- |\n" +
            string.Concat(methods.Select((row, index) =>
                $"| N-{40 + index} | x; `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.{row.Method}` | 401 | D | #539 |\n"));

        Dictionary<string, MethodInfo> accepted = DeployedSubsetTests.ContinuityRowTests(register, builder.CreateType());

        Assert.Equal("Row_fact_task_no_parameters", Assert.Single(accepted).Value.Name);
        Assert.Equal("N-40", Assert.Single(accepted).Key);
    }

    /// <summary>An export page with no D row, so any row the run takes below comes from the register.</summary>
    private const string ExportPageWithNoDRow = """
        ## Negative security tests

        | ID | Attempt | Expect | Tier |
        | --- | --- | --- | --- |
        | N-02 | Forge a cookie | 401 | H |
        """;

    [Fact]
    public void A_register_d_row_joins_the_run_with_its_negative_test()
    {
        // The register's N table (#322) is "None yet." on the tree, so the real theory cannot show this:
        // a row the register alone marks D must run on the deployed stack as the export's do (#389).
        const string register = """
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            | N-26 | Login CSRF | Rejected | D | #322 Q8 |
            """;

        Assert.Equal(
            ["CookiePlantingTests.N26_login_csrf_a_siblings_login_state_cookies_are_never_set_so_the_planted_callback_is_rejected"],
            RowTests(ExportPageWithNoDRow, register).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_register_d_row_with_no_negative_test_fails_the_run_naming_its_id()
    {
        const string register = """
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            | N-28 | Plant a D cookie | Refused | H, D | #322 Q8 |
            """;
        const string page = """
            ## Negative security tests

            | ID | Attempt | Expect | Tier |
            | --- | --- | --- | --- |
            | N-26 | Login CSRF | Rejected | D |
            """;

        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEveryDRowHasATest(page, register, RowTests(page, register).Keys));
        Assert.Contains("N-28", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("N-26", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("None yet.")]
    [InlineData("| N-26 | Login CSRF | Rejected | H | #322 Q8 |")]
    public void A_register_with_no_n_rows_or_none_marked_d_adds_no_row_to_the_run(string body)
    {
        string register = $"""
            ## Negative security (N)

            | ID | Attempt | Expect | Tier | Recorded by |
            | --- | --- | --- | --- | --- |
            {body}
            """;

        Assert.Empty(RowTests(ExportPageWithNoDRow, register));
        AssertEveryDRowHasATest(ExportPageWithNoDRow, register, []);
    }

    /// <summary>
    /// Fails, naming each id, when a D row of the export's negative table or of the register's (#322) has
    /// no test among <paramref name="tests"/>, each named by class and method.
    /// </summary>
    internal static void AssertEveryDRowHasATest(string page, string register, IEnumerable<string> tests)
    {
        HashSet<string> names = new(tests, StringComparer.Ordinal);
        Dictionary<string, MethodInfo> continuity = DeployedSubsetTests.ContinuityRowTests(register);
        string[] untested = [.. DeployedSubsetTests.DeployedRowIds(page, register)
            .Where(row => !(continuity.TryGetValue(row, out MethodInfo? method) && names.Contains($"{method.DeclaringType!.Name}.{method.Name}")) &&
                !names.Any(test => test.Contains($".N{row[2..]}_", StringComparison.Ordinal)))];
        Assert.Empty(untested);
    }

    /// <summary>The negative tests whose row the Tests tab or the register marks D, by class and method.</summary>
    private static Dictionary<string, MethodInfo> RowTests() => RowTests(TestsPage(), RegisterPage());

    /// <summary>
    /// The negative tests whose row <paramref name="page"/>'s negative table or <paramref name="register"/>'s
    /// marks D, by class and method: a later N- id lives in the register alone (#322), and its D row runs
    /// here as the export's do (#389).
    /// </summary>
    internal static Dictionary<string, MethodInfo> RowTests(string page, string register)
    {
        string[] rows = DeployedSubsetTests.DeployedRowIds(page, register);
        Dictionary<string, MethodInfo> tests = typeof(CookiePlantingTests).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(CookiePlantingTests).Namespace && type.IsPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null && rows.Any(row => method.Name.StartsWith($"N{row[2..]}_", StringComparison.Ordinal)))
            .ToDictionary(method => $"{method.DeclaringType!.Name}.{method.Name}", StringComparer.Ordinal);
        foreach (MethodInfo method in DeployedSubsetTests.ContinuityRowTests(register).Values)
        {
            tests.TryAdd($"{method.DeclaringType!.Name}.{method.Name}", method);
        }

        return tests;
    }

    internal static Dictionary<string, string> StandInExclusions(string register)
    {
        Dictionary<string, string> exclusions = new(OutsideStandIn, StringComparer.Ordinal);
        foreach (MethodInfo method in DeployedSubsetTests.ContinuityRowTests(register).Values)
        {
            exclusions.Add($"{method.DeclaringType!.Name}.{method.Name}",
                "requires the real two-task continuity driver and task-correlated evidence; the stand-in has one host (#657)");
        }

        return exclusions;
    }

    private static string TestsPage() => File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "docs", "plan", "04-tests.md"));

    private static string RegisterPage() => File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "test-ids.md"));

    /// <summary>A test's cases: none for a fact, its inline data or its member's rows for a theory.</summary>
    private static object?[][] CasesOf(MethodInfo method)
    {
        if (method.GetCustomAttribute<TheoryAttribute>() is null)
        {
            return [[]];
        }

        List<object?[]> cases = [.. method.GetCustomAttributes<InlineDataAttribute>().Select(inline => inline.Data)];
        foreach (MemberDataAttribute member in method.GetCustomAttributes<MemberDataAttribute>())
        {
            object rows = method.DeclaringType!.GetProperty(member.MemberName, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            cases.AddRange(((IEnumerable)rows).Cast<object>().Select(row => row is ITheoryDataRow data ? data.GetData() : (object?[])row));
        }

        Assert.NotEmpty(cases);
        return [.. cases];
    }

    /// <summary>The deployed stacks a row started, each over a hermetic cell of its own.</summary>
    private sealed class Cells(Func<JsonElement, bool>? shipped = null) : IAsyncDisposable
    {
        private readonly List<Run> runs = [];

        public IReadOnlyList<Run> Runs => runs;

        public async Task<IBrowserStack> StartAsync()
        {
            Run run = await Run.StartAsync(shipped ?? (_ => true));
            runs.Add(run);
            return run.Stack;
        }

        /// <summary>The cells behind the stacks; each row disposes its own stack.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (Run run in runs)
            {
                await run.Tunnel.DisposeAsync();
                await run.Harness.DisposeAsync();
            }
        }
    }

    /// <summary>A deployed stack over the hermetic cell: the harness, its stand-in tunnel, the recorded runner, and the stack.</summary>
    private sealed record Run(GatewayHarness Harness, TlsLoadBalancer Tunnel, RecordedRunner Runner, DeployedStack Stack)
    {
        public static async Task<Run> StartAsync(Func<JsonElement, bool> shipped)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            GatewayHarness harness = await GatewayHarness.StartAsync(null, ct);
            TlsLoadBalancer? tunnel = null;
            try
            {
                tunnel = await TlsLoadBalancer.StartAsync(harness.GatewayAddress, LocalStack.Domain, ct);
                Cell cell = CellOf(harness);
                RecordedRunner runner = new(arguments => arguments is ["logs", "filter-log-events", ..]
                    ? CloudWatch(harness, cell, arguments, shipped)
                    : throw new InvalidOperationException($"no recorded answer for aws {string.Join(' ', arguments)}"));
                string spki = tunnel.Spki;
                DeployedStack stack = await DeployedStack.StartAsync(new DeployedStackSetup(
                    cell,
                    Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json"),
                    runner,
                    int.Parse(tunnel.Endpoint.Split(':')[1], CultureInfo.InvariantCulture))
                {
                    // The workstation trusts the ALB through certs.sh's authority; the stand-in by its key.
                    TrustedKeys = [spki],
                    RawValidation = (_, certificate, _, _) => certificate is X509Certificate2 presented && ForeignSites.SpkiOf(presented) == spki,
                    // Each run's recorded log is its own, so no line of an earlier stack can reach it.
                    Quiet = TimeSpan.Zero,
                    // The captured log holds a line within milliseconds of its request, as CloudWatch does not.
                    RuleArrival = TimeSpan.FromSeconds(10),
                });
                return new Run(harness, tunnel, runner, stack);
            }
            catch
            {
                if (tunnel is not null)
                {
                    await tunnel.DisposeAsync();
                }

                await harness.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// The outputs document's cell with the harness's clients: the contract's example, whose provider
        /// is a Cognito domain the browser never reaches, since the test provider approves at once.
        /// </summary>
        private static Cell CellOf(GatewayHarness harness)
        {
            JsonObject document = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json")))!.AsObject();
            JsonObject clients = [];
            foreach (string appId in new[] { "app1", "app2" })
            {
                clients[appId] = new JsonObject { ["clientId"] = harness.App(appId).ClientId };
            }

            document["clients"] = clients;
            return Cell.Parse(document.ToJsonString());
        }

        /// <summary>
        /// What <c>aws logs filter-log-events</c> answers for the cell's log group and the gateway's
        /// stream prefix from <c>--start-time</c>: the gateway's lines, stamped with their own <c>ts</c>.
        /// </summary>
        private static string CloudWatch(GatewayHarness harness, Cell cell, string[] arguments, Func<JsonElement, bool> shipped)
        {
            Assert.Equal(cell.LogGroup, arguments[Array.IndexOf(arguments, "--log-group-name") + 1]);
            Assert.Equal(CellAws.GatewayStreamPrefix, arguments[Array.IndexOf(arguments, "--log-stream-name-prefix") + 1]);
            long since = long.Parse(arguments[Array.IndexOf(arguments, "--start-time") + 1], CultureInfo.InvariantCulture);

            var events = harness.Log.Json
                .Select(line => (At: DateTimeOffset.Parse(line.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), Line: line))
                .Where(entry => entry.At >= since && shipped(entry.Line))
                .Select((entry, i) => new { logStreamName = "gateway/gateway/local", timestamp = entry.At, message = entry.Line.GetRawText(), eventId = i.ToString(CultureInfo.InvariantCulture) });
            return JsonSerializer.Serialize(new { events });
        }
    }
}
