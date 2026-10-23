using System.Collections;
using System.Globalization;
using System.Reflection;
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
    /// Cognito's own, which a sitting alone shows. The stand-in's provider is the test provider, which
    /// approves at once and serves its endpoints at its own paths.
    /// </summary>
    private static readonly Dictionary<string, string> CognitoOnly = new(StringComparer.Ordinal)
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

    /// <summary>Every negative test of a D row, by class and method, but those <see cref="CognitoOnly"/> names.</summary>
    public static TheoryData<string> DeployedRowTests => [.. RowTests().Keys.Where(test => !CognitoOnly.ContainsKey(test)).Order(StringComparer.Ordinal)];

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
        Assert.All(DeployedSubsetTests.DeployedRows(TestsPage()), row =>
            Assert.Contains(tests.Keys, test => test.Contains($".N{row[2..]}_", StringComparison.Ordinal)));
        Assert.All(CognitoOnly, left => Assert.True(tests.ContainsKey(left.Key) && left.Value.Length > 0, $"{left.Key} is not a D row's test."));
    }

    /// <summary>The negative tests whose row the Tests tab marks D, by class and method.</summary>
    private static Dictionary<string, MethodInfo> RowTests()
    {
        string[] rows = DeployedSubsetTests.DeployedRows(TestsPage());
        return typeof(CookiePlantingTests).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(CookiePlantingTests).Namespace && type.IsPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null && rows.Any(row => method.Name.StartsWith($"N{row[2..]}_", StringComparison.Ordinal)))
            .ToDictionary(method => $"{method.DeclaringType!.Name}.{method.Name}", StringComparer.Ordinal);
    }

    private static string TestsPage() => File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "docs", "plan", "04-tests.md"));

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
