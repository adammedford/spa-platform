using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// The sitting browser's command surface and observation line (#1033) on its process (#1032), with no
/// cell: the resident process runs in this test process on the local stack, and every command reaches
/// it as a client call over its socket, as a sitting's Bash calls do. The executable facts run the
/// suite's own executable, which is what a sitting starts. The password's path (<c>DeployedStack</c>'s
/// login form) is proven in the first sitting on the cell (#1027), since no provider here shows a form.
/// </summary>
public sealed partial class SittingBrowserTests
{
    /// <summary>How long a stop may take: the profiles, the browser and the local cell closing.</summary>
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);

    /// <summary>How long the built executable may take to print its ready line: the browser installer's check, the cell, Chromium.</summary>
    private static readonly TimeSpan ReadyBound = TimeSpan.FromMinutes(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_executable_lists_with_the_console_runner_every_test_method_the_assembly_declares()
    {
        // -list methods prints each test method once, a theory once whatever its rows, as the
        // reflection below counts them; its JSON is the first line, and a colour reset may follow.
        ChildResult listed = await RunExecutableAsync(["-list", "methods/json", "-noColor"], atReady: null, TimeSpan.FromMinutes(2));
        Assert.Equal(0, listed.ExitCode);
        string json = listed.Output.Split('\n')[0];
        HashSet<string> methods = [.. JsonSerializer.Deserialize<string[]>(json)!];

        HashSet<string> declared = [.. typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any())
                .Select(method => $"{type.FullName}.{method.Name}"))];

        Assert.NotEmpty(declared);
        Assert.Contains($"{typeof(SittingBrowserTests).FullName}.{nameof(The_executable_lists_with_the_console_runner_every_test_method_the_assembly_declares)}", declared);
        Assert.Equal(declared.Order(StringComparer.Ordinal), methods.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Each_command_answers_with_its_line_in_the_grammar_on_the_local_stack()
    {
        await using Running sitting = await Running.StartAsync();
        HarnessApp app = sitting.Engine.Stack.App("app1");
        string api = FixtureApi("app1");
        const string Readiness = "revision=[0-9]+ instance=[A-Za-z0-9-]+";

        // The test provider approves at once, so the shell's challenge on / signs the profile in.
        Answer opened = await sitting.SendAsync("open", "main", "--app", "app1");
        opened.Assert(0, Line("signed-in", "main", $"app=app1 {Readiness}"));

        Answer login = await sitting.SendAsync("login", "main", "--app", "app1", "--as", "bob");
        login.Assert(0, Line("signed-in", "main", $"app=app1 as=bob {Readiness}"));

        Answer reload = await sitting.SendAsync("reload", "main");
        reload.Assert(0, Line("signed-in", "main", $"app=app1 as=bob {Readiness}"));

        Answer marker = await sitting.SendAsync("marker", "main");
        marker.Assert(0, Line("marker", "main", $"app=app1 text={Regex.Escape(FixtureMarker("app1"))}"));

        Answer session = await sitting.SendAsync("session", "main");
        session.Assert(0, Line("session", "main", "app=app1 http=200 problem=-"));

        Answer whoami = await sitting.SendAsync("api", "main", $"{api}/whoami");
        whoami.Assert(0, Line("api", "main", $"app=app1 path=/api/{api}/whoami http=200 problem=-"));

        // One cookie on the provider's host, which the test provider sets none of by default.
        IBrowserContext context = sitting.Engine.ProfileNamed("main")!.Context;
        await context.AddCookiesAsync([new Cookie { Name = "provider-probe", Value = "provider-cookie-value", Url = sitting.Engine.Stack.AuthorizeEndpoint }]);
        IReadOnlyList<BrowserContextCookiesResult> appCookies = await context.CookiesAsync([app.Origin.ToString()]);
        Assert.NotEmpty(appCookies);
        Answer cleared = await sitting.SendAsync("clear-provider", "main");
        cleared.Assert(0, Line("clear-provider", "main", "app=app1 removed=1"));
        Assert.Equal(appCookies.Select(cookie => cookie.Name).Order(), (await context.CookiesAsync([app.Origin.ToString()])).Select(cookie => cookie.Name).Order());
        Assert.Empty(await context.CookiesAsync([sitting.Engine.Stack.AuthorizeEndpoint]));

        Answer status = await sitting.SendAsync("status");
        status.Assert(0, Line("status", "main", $"app=app1 last=clear-provider at={TimePattern}"));

        Answer closed = await sitting.SendAsync("close", "main");
        closed.Assert(0, Line("closed", "main", "app=app1"));

        Answer none = await sitting.SendAsync("status");
        none.Assert(0, Line("status", "-", "profiles=0"));

        Answer unknown = await sitting.SendAsync("marker", "main");
        unknown.Assert(1, Line("failed", "main", "command=marker reason=unknown-profile"));

        await sitting.SendAsync("open", "other", "--app", "app1");
        Answer stopped = await sitting.SendAsync("stop");
        stopped.Assert(0, Line("closed", "other", "app=app1"));
        Assert.Equal(0, await sitting.Run.WaitAsync(StopBound, Ct));
        Assert.Matches(Line("stopped", "-", "reason=stop"), sitting.ServerLines.Lines[^1]);

        foreach (string line in sitting.Everything)
        {
            AssertGrammar(line);
        }
    }

    [Fact]
    public async Task Every_line_shape_comes_out_of_the_redactor_unchanged_in_stream_and_post_mode()
    {
        string[] shapes = [.. LineShapes().Select(shape => shape.Line)];
        HashSet<string> sampled = [.. LineShapes().Select(shape => shape.Member)];
        HashSet<string> members = [.. typeof(Observations).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(string)).Select(method => method.Name)];
        Assert.Equal(members.Order(StringComparer.Ordinal), sampled.Order(StringComparer.Ordinal));
        foreach (string line in shapes)
        {
            AssertGrammar(line);
        }

        string input = string.Join('\n', shapes) + "\n";
        (int streamExit, string streamed) = await RedactAsync(["--stream"], input);
        Assert.Equal(0, streamExit);
        Assert.Equal(input, streamed);

        // Post mode holds any post naming AccessDenied (exit 3) by design, so the SSM refusal's line is fed to the stream alone.
        string postInput = string.Join('\n', shapes.Where(line => !line.Contains("AccessDenied", StringComparison.Ordinal))) + "\n";
        (int postExit, string posted) = await RedactAsync([], postInput);
        Assert.Equal(0, postExit);
        Assert.Equal(postInput, posted);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("code")]
    [InlineData("id")]
    [InlineData("value")]
    [InlineData("client_id")]
    [InlineData("State")]
    public void A_line_refuses_a_key_the_redactor_elides(string key)
    {
        Assert.Throws<ArgumentException>(() => ObservationLine.Format("other", DateTimeOffset.UtcNow, "main", [(key, "x")]));
        Assert.Throws<ArgumentException>(() => ObservationLine.Format("other", DateTimeOffset.UtcNow, "main", [], (key, "x y")));
    }

    [Fact]
    public async Task Watch_prints_one_line_a_tick_at_its_interval_appends_them_to_the_log_and_stops_on_unwatch()
    {
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));

        TimedLines watched = new();
        string[] words = ["watch", "main", "--every", "2s", "--clear-provider", "--relogin"];
        Task<int> watch = SittingBrowserClient.SendAsync(sitting.Bundle.FullName, SittingCommand.Parse(words).Command!, words, watched, Ct);
        await watched.WaitForCountAsync(3, watch);

        Answer unwatched = await sitting.SendAsync("unwatch", "main");
        Assert.Equal(0, await watch.WaitAsync(StopBound, Ct));
        unwatched.Assert(0, Line("unwatched", "main", "app=app1 ticks=[0-9]+"));

        (string Line, TimeSpan At)[] ticks = [.. watched.Lines];
        Assert.True(ticks.Length >= 3, $"{ticks.Length} lines");
        Assert.All(ticks, tick => Assert.Matches(Line("signed-in", "main", "app=app1 revision=[0-9]+ instance=[A-Za-z0-9-]+"), tick.Line));
        foreach (((string _, TimeSpan earlier), (string _, TimeSpan later)) in ticks.Zip(ticks.Skip(1)))
        {
            TimeSpan gap = later - earlier;
            Assert.True(gap >= TimeSpan.FromSeconds(1) && gap <= TimeSpan.FromSeconds(4), $"a tick {gap.TotalSeconds:F1}s after the last, at an interval of 2s.");
        }

        Assert.Equal(ticks.Length, int.Parse(TicksField().Match(unwatched.Lines[0]).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        string log = Path.Combine(sitting.Bundle.FullName, SittingBrowser.WatchLog);
        Assert.Equal(ticks.Select(tick => tick.Line), await File.ReadAllLinesAsync(log, Ct));

        // Nothing more after unwatch, for longer than two intervals.
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ticks.Length, (await File.ReadAllLinesAsync(log, Ct)).Length);
        Assert.Equal(ticks.Length, watched.Lines.Count);
        (await sitting.SendAsync("unwatch", "main")).Assert(1, Line("failed", "main", "command=unwatch reason=not-watching"));
    }

    /// <summary>
    /// A closed page's reload is a <see cref="PlaywrightException"/> with no <c>net::ERR_</c> in it, so it
    /// is no <c>tunnel-down</c> and the tick fails. The page closes just after a line, between ticks,
    /// where no read of the page can turn the failure into an observation.
    /// </summary>
    [Fact]
    public async Task A_tick_that_fails_ends_the_watch_with_its_failed_line_last_in_the_log_and_at_the_caller()
    {
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));

        TimedLines watched = new();
        string[] words = ["watch", "main", "--every", "2s"];
        Task<int> watch = SittingBrowserClient.SendAsync(sitting.Bundle.FullName, SittingCommand.Parse(words).Command!, words, watched, Ct);
        await watched.WaitForCountAsync(1, watch);
        await sitting.Engine.ProfileNamed("main")!.Page.CloseAsync();
        Assert.Equal(1, await watch.WaitAsync(StopBound, Ct));

        string[] printed = [.. watched.Lines.Select(line => line.Line)];
        Assert.True(printed.Length >= 2, string.Join(" | ", printed));
        Assert.Matches(Line("signed-in", "main", "app=app1 .*"), printed[0]);
        Assert.Matches(Line("failed", "main", "command=watch reason=PlaywrightException"), printed[^1]);
        string log = Path.Combine(sitting.Bundle.FullName, SittingBrowser.WatchLog);
        Assert.Equal(printed, await File.ReadAllLinesAsync(log, Ct));

        // The unwatch D2 runs next answers its caller alone, so the failed line stays the log's last.
        (await sitting.SendAsync("unwatch", "main")).Assert(0, Line("unwatched", "main", "app=app1 ticks=[0-9]+"));
        Assert.Equal(printed, await File.ReadAllLinesAsync(log, Ct));
    }

    [Fact]
    public async Task A_tick_that_fails_after_its_caller_has_gone_still_writes_its_failed_line_to_the_log()
    {
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));

        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        TimedLines watched = new();
        string[] words = ["watch", "main", "--every", "2s"];
        Task<int> watch = SittingBrowserClient.SendAsync(sitting.Bundle.FullName, SittingCommand.Parse(words).Command!, words, watched, caller.Token);
        await watched.WaitForCountAsync(1, watch);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
        await sitting.Engine.ProfileNamed("main")!.Page.CloseAsync();

        string log = Path.Combine(sitting.Bundle.FullName, SittingBrowser.WatchLog);
        Stopwatch waited = Stopwatch.StartNew();
        string[] written = await File.ReadAllLinesAsync(log, Ct);
        while (!written[^1].StartsWith("failed", StringComparison.Ordinal) && waited.Elapsed < StopBound)
        {
            await Task.Delay(100, Ct);
            written = await File.ReadAllLinesAsync(log, Ct);
        }

        Assert.Matches(Line("failed", "main", "command=watch reason=PlaywrightException"), written[^1]);
    }

    /// <summary>
    /// An AWS failure's line is the one that carries free text, and the log does not pass the redactor
    /// the caller's stream does, so the account stays masked by the exception's own message. A sitting's
    /// AWS failure is the password's read, which needs the deployed stack's form (the class's summary);
    /// here the same exception comes from the stack's <c>ProviderHost</c>, which <c>--clear-provider</c>
    /// reads on every tick.
    /// </summary>
    [Fact]
    public async Task A_tick_that_fails_on_aws_writes_its_failed_line_with_the_masked_message_to_the_log()
    {
        FailingStack? failing = null;
        await using Running sitting = await Running.StartAsync(async ct =>
        {
            failing = FailingStack.Over(await Stacks.StartCancellableAsync(ct));
            return (IBrowserStack)(object)failing;
        });
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));

        TimedLines watched = new();
        string[] words = ["watch", "main", "--every", "2s", "--clear-provider"];
        Task<int> watch = SittingBrowserClient.SendAsync(sitting.Bundle.FullName, SittingCommand.Parse(words).Command!, words, watched, Ct);
        await watched.WaitForCountAsync(1, watch);
        failing!.ProviderHostFailure = new CommandFailedException("aws", ["ssm", "get-parameter"], 254, "AccessDenied on 123456789012");
        Assert.Equal(1, await watch.WaitAsync(StopBound, Ct));

        string[] printed = [.. watched.Lines.Select(line => line.Line)];
        Assert.Matches(Line("failed", "main", "command=watch reason=aws text=.*AccessDenied on \\*{12}.*"), printed[^1]);
        Assert.Equal(printed, await File.ReadAllLinesAsync(Path.Combine(sitting.Bundle.FullName, SittingBrowser.WatchLog), Ct));
    }

    [Fact]
    public async Task A_failed_line_outside_a_watch_goes_to_the_caller_alone()
    {
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));
        (await sitting.SendAsync("open", "other", "--app", "app1")).Assert(0, Line("signed-in", "other", "app=app1 .*"));

        TimedLines watched = new();
        string[] words = ["watch", "main", "--every", "2s"];
        Task<int> watch = SittingBrowserClient.SendAsync(sitting.Bundle.FullName, SittingCommand.Parse(words).Command!, words, watched, Ct);
        await watched.WaitForCountAsync(1, watch);

        // With a log to write to: a command that fails as a tick does, and the two watches that never start.
        await sitting.Engine.ProfileNamed("other")!.Page.CloseAsync();
        (await sitting.SendAsync("reload", "other")).Assert(1, Line("failed", "other", "command=reload reason=PlaywrightException"));
        (await sitting.SendAsync("watch", "main", "--every", "2s")).Assert(1, Line("failed", "main", "command=watch reason=already-watching"));
        (await sitting.SendAsync("watch", "nobody", "--every", "2s")).Assert(1, Line("failed", "nobody", "command=watch reason=unknown-profile"));

        (await sitting.SendAsync("unwatch", "main")).Assert(0, Line("unwatched", "main", "app=app1 ticks=[0-9]+"));
        Assert.Equal(0, await watch.WaitAsync(StopBound, Ct));
        string[] printed = [.. watched.Lines.Select(line => line.Line)];
        Assert.DoesNotContain(printed, line => line.StartsWith("failed", StringComparison.Ordinal));
        Assert.Equal(printed, await File.ReadAllLinesAsync(Path.Combine(sitting.Bundle.FullName, SittingBrowser.WatchLog), Ct));
    }

    /// <summary>
    /// The provider's submit is answered two seconds later, as Cognito's is some time later on the cell:
    /// the login's observation is of the page the login ended on, not of the form it left (#1195).
    /// </summary>
    [Fact]
    public async Task Login_prints_signed_in_when_the_providers_submit_answers_after_a_delay()
    {
        await using Fronted sitting = await Fronted.StartAsync();
        sitting.Form.AnswersAfter = TimeSpan.FromSeconds(2);

        Answer login = await sitting.SendAsync("login", "main", "--app", "app1");

        login.Assert(0, Line("signed-in", "main", "app=app1 as=[a-z]+ revision=[0-9]+ instance=[A-Za-z0-9-]+"));
        Assert.Equal(1, sitting.Form.Submitted);
        Assert.Single(login.Lines);
    }

    [Fact]
    public async Task Login_prints_timeout_when_the_providers_submit_never_answers()
    {
        await using Fronted sitting = await Fronted.StartAsync(bound: TimeSpan.FromSeconds(5));
        sitting.Form.AnswersAfter = null;

        Answer login = await sitting.SendAsync("login", "main", "--app", "app1");

        login.Assert(1, Line("timeout", "main", "app=app1"));
        Assert.Equal(1, sitting.Form.Submitted);
        Assert.Single(login.Lines);
    }

    /// <summary>
    /// The submit's answer takes longer than the watch's interval, as the cell's did in the sitting of
    /// 2026-11-13 (#348's D2): a re-login read at the click is <c>login-prompt</c>, and the next tick's
    /// reload then abandons the submit, so no tick ever reads <c>signed-in</c> (#1195).
    /// </summary>
    [Fact]
    public async Task A_watch_that_signs_out_and_logs_in_again_reads_signed_in_when_the_providers_submit_answers_after_a_delay()
    {
        await using Fronted sitting = await Fronted.StartAsync();
        sitting.Form.InTheWay = false;
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));
        sitting.Form.InTheWay = true;
        sitting.Form.AnswersAfter = TimeSpan.FromSeconds(3);

        Task<Answer> watch = sitting.SendAsync("watch", "main", "--every", "2s", "--clear-provider", "--relogin");
        await sitting.WaitForAsync(lines => lines.Any(line => line.StartsWith("signed-in", StringComparison.Ordinal)), "a tick that reads signed-in before the sign-out");
        int before = sitting.Lines.Count;

        // Signed out of the app and of the provider at once: the browser holds nothing of either.
        await sitting.Engine.ProfileNamed("main")!.Context.ClearCookiesAsync();
        await sitting.WaitForAsync(
            lines => lines.Skip(before).SkipWhile(line => !line.StartsWith("login-prompt", StringComparison.Ordinal)).Skip(2).Any(),
            "two lines after the sign-out's login-prompt");
        (await sitting.SendAsync("unwatch", "main")).Assert(0, Line("unwatched", "main", "app=app1 ticks=[0-9]+"));
        Assert.Equal(0, (await watch.WaitAsync(StopBound, Ct)).Exit);

        // The tick that met the form, then its re-login, then the ticks after it: signed in from there on.
        string[] after = [.. sitting.Lines.Skip(before).SkipWhile(line => !line.StartsWith("login-prompt", StringComparison.Ordinal))
            .TakeWhile(line => !line.StartsWith("unwatched", StringComparison.Ordinal))];
        Assert.Matches(Line("login-prompt", "main", "app=app1 revision=- instance=-"), after[0]);
        Assert.All(after.Skip(1), line => Assert.Matches(Line("signed-in", "main", "app=app1 revision=[0-9]+ instance=[A-Za-z0-9-]+"), line));
        Assert.Equal(1, sitting.Form.Submitted);
    }

    /// <summary>
    /// The gateway refuses a callback whose state it never issued with its failure page, a 400 that is no
    /// problem body (contracts/http-contract.md -> Errors). The line says so, and neither it nor the
    /// saved page carries the code or the state the URL does.
    /// </summary>
    [Fact]
    public async Task An_other_at_the_callback_path_carries_the_refusals_status_and_no_code_or_state_value()
    {
        const string Code = "canary-code-1195-4f2a9c";
        const string State = "canary-state-1195-b81d3e";
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));
        Profile profile = sitting.Engine.ProfileNamed("main")!;
        await profile.GotoAsync(sitting.Engine.Stack.App("app1").Url($"/signin-oidc?code={Code}&state={State}").ToString());

        Answer reload = await sitting.SendAsync("reload", "main");

        reload.Assert(0, Line("other", "main", "app=app1 host=app1 path=/signin-oidc http=400 problem=- revision=[0-9]+ instance=[A-Za-z0-9-]+"));
        await AssertNothingHoldsAsync(sitting.Bundle.FullName, sitting.Everything, Code, State);
    }

    /// <summary>
    /// A problem document's <c>code</c> and <c>title</c> name the refusal. The gateway's own problem
    /// bodies carry a status and a code and no title (<c>Problems.WriteAsync</c>), so the document here is
    /// the test's, answered in the gateway's place, with a title that repeats the URL's state and a URL
    /// with the code in it: the worst a title could hold.
    /// </summary>
    [Fact]
    public async Task An_other_at_the_callback_path_carries_a_problem_documents_code_and_title_with_no_code_or_state_value()
    {
        const string Code = "canary-code-1195-7d3e1b";
        const string State = "canary-state-1195-c05a2f";
        await using Running sitting = await Running.StartAsync();
        (await sitting.SendAsync("open", "main", "--app", "app1")).Assert(0, Line("signed-in", "main", "app=app1 .*"));
        Profile profile = sitting.Engine.ProfileNamed("main")!;
        Uri callback = sitting.Engine.Stack.App("app1").Url($"/signin-oidc?code={Code}&state={State}");
        string problem = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["status"] = 403,
            ["code"] = "origin_rejected",
            ["title"] = $"The request gate refused\nthe callback for {State} at {callback}",
        });
        await profile.Context.RouteAsync("**/signin-oidc*", route => route.FulfillAsync(new() { Status = 403, ContentType = "application/problem+json", Body = problem }));
        await profile.GotoAsync(callback.ToString());

        Answer reload = await sitting.SendAsync("reload", "main");

        reload.Assert(0, Line("other", "main",
            $"app=app1 host=app1 path=/signin-oidc http=403 problem=origin_rejected revision=[0-9]+ instance=[A-Za-z0-9-]+ title=The request gate refused the callback for {Regex.Escape(Redacted.Removed)} at {Regex.Escape(Redacted.Url(callback))}"));
        AssertGrammar(reload.Lines[^1]);
        await AssertNothingHoldsAsync(sitting.Bundle.FullName, sitting.Everything, Code, State);
    }

    /// <summary>No line, and no page saved for an <c>other</c>, of which there is at least one, holds any of <paramref name="values"/> or a URL with a query.</summary>
    private static async Task AssertNothingHoldsAsync(string bundle, IEnumerable<string> lines, params string[] values)
    {
        string[] pages = Directory.GetFiles(Path.Combine(bundle, SittingBrowser.PagesDirectory));
        Assert.NotEmpty(pages);
        List<string> texts = [string.Join('\n', lines)];
        foreach (string page in pages)
        {
            texts.Add(await File.ReadAllTextAsync(page, Ct));
        }

        foreach (string text in texts)
        {
            Assert.All(values, value => Assert.DoesNotContain(value, text, StringComparison.Ordinal));
            Assert.DoesNotMatch(UrlWithQuery(), text);
        }
    }

    [Fact]
    public async Task What_it_prints_holds_no_cookie_value_no_url_with_a_query_and_no_csrf_token()
    {
        await using Running sitting = await Running.StartAsync();
        await sitting.SendAsync("login", "main", "--app", "app1");
        await sitting.SendAsync("reload", "main");
        await sitting.SendAsync("marker", "main");
        (await sitting.SendAsync("session", "main")).Assert(0, Line("session", "main", "app=app1 http=200 problem=-"));
        await sitting.SendAsync("api", "main", $"{FixtureApi("app1")}/whoami");
        await sitting.SendAsync("status");

        Profile profile = sitting.Engine.ProfileNamed("main")!;
        IReadOnlyList<BrowserContextCookiesResult> cookies = await profile.Context.CookiesAsync();

        // The tokens the browser itself received, the session command's fetch among them: /bff/session
        // mints a fresh request token on every call (PlatformAntiforgery's GetAndStoreTokens), so a
        // fetch of the test's own would hold a token no line could have carried (#1033's review).
        string[] csrfTokens = [.. (await sitting.Engine.Stack.ExchangesAsync())
            .Where(exchange => exchange.Url.AbsolutePath == "/bff/session" && exchange.Status == 200)
            .Select(exchange => JsonDocument.Parse(exchange.BodyText).RootElement.GetProperty("csrfToken").GetString()!)];
        await sitting.SendAsync("stop");
        await sitting.Run.WaitAsync(StopBound, Ct);

        string text = string.Join('\n', sitting.Everything);
        Assert.NotEmpty(cookies);
        foreach (string value in cookies.Select(cookie => cookie.Value).Where(value => value.Length > 0))
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }

        Assert.NotEmpty(csrfTokens);
        foreach (string csrf in csrfTokens)
        {
            Assert.True(csrf.Length >= 16);
            Assert.DoesNotContain(csrf, text, StringComparison.Ordinal);
        }
        Assert.DoesNotMatch(UrlWithQuery(), text);
    }

    [Fact]
    public async Task The_server_log_holds_no_url_with_a_query_no_csrf_token_and_no_cookie_value()
    {
        // The executable's own output, the local cell's hosts among it, goes to the server log. Its
        // cookie values are the child's, out of this process's reach, so they are read back with the
        // browser's own cookie header shape: no Cookie or Set-Cookie line, no session cookie's name with a value.
        using TempBundle bundle = new();
        ChildResult child = await RunExecutableAsync(Start(bundle), atReady: async _ =>
        {
            Assert.Equal(0, (await RunClientAsync(bundle, ["login", "main", "--app", "app1"])).ExitCode);
            ChildResult session = await RunClientAsync(bundle, ["session", "main"]);
            Assert.Equal(0, session.ExitCode);
            Assert.Matches(Line("session", "main", "app=app1 http=200 problem=-"), session.Output.TrimEnd('\n'));
            Assert.Equal(0, (await RunClientAsync(bundle, ["reload", "main"])).ExitCode);
            Assert.Equal(0, (await RunClientAsync(bundle, ["stop"])).ExitCode);
        }, ReadyBound);
        Assert.True(child.ExitCode == 0, $"exit {child.ExitCode}; standard error: {child.Error}");

        string log = await File.ReadAllTextAsync(Path.Combine(bundle.Path, SittingBrowserServer.ServerLog), Ct);
        Assert.NotEmpty(log);
        Assert.DoesNotMatch(UrlWithQuery(), log);
        Assert.DoesNotContain("csrfToken", log, StringComparison.Ordinal);
        Assert.DoesNotMatch(CookieWithValue(), log);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(bundle.Path, SittingBrowserServer.ServerLog)));
        }
    }

    [Fact]
    public async Task A_client_with_no_socket_prints_not_running_and_exits_1()
    {
        using TempBundle bundle = new();
        string[] words = ["marker", "main"];
        StringWriter output = new();
        Assert.Equal(1, await SittingBrowserClient.SendAsync(bundle.Path, SittingCommand.Parse(words).Command!, words, output, Ct));
        Assert.Matches(Line("not-running", "main", ""), output.ToString().TrimEnd('\n'));

        // A socket file nothing listens on, a crashed process's, is the same.
        await File.WriteAllTextAsync(Path.Combine(bundle.Path, SittingBrowserServer.SocketName), "", Ct);
        output = new();
        Assert.Equal(1, await SittingBrowserClient.SendAsync(bundle.Path, SittingCommand.Parse(words).Command!, words, output, Ct));
        Assert.Matches(Line("not-running", "main", ""), output.ToString().TrimEnd('\n'));

        // The executable, as a sitting calls it.
        ChildResult child = await RunClientAsync(bundle, ["status"]);
        Assert.Equal(1, child.ExitCode);
        Assert.Matches(Line("not-running", "-", ""), child.Output.TrimEnd('\n'));
    }

    [Fact]
    public async Task A_second_start_on_a_live_socket_refuses_and_leaves_the_first_running()
    {
        await using Running sitting = await Running.StartAsync();
        CapturedLines second = new();
        int exit = await SittingBrowserServer.RunAsync(sitting.Bundle.FullName, second,
            _ => throw new InvalidOperationException("a refused start started a stack."), NoEnvironment, Ct);

        Assert.Equal(1, exit);
        Assert.Matches(Line("refused", "-", "reason=running"), Assert.Single(second.Lines));
        (await sitting.SendAsync("status")).Assert(0, Line("status", "-", "profiles=0"));
    }

    [Theory]
    [InlineData("PWDEBUG", "1", "pwdebug")]
    [InlineData("PWDEBUG", "", "pwdebug")]
    [InlineData("DEBUG", "pw:api", "debug-pw")]
    [InlineData("DEBUG", "express:*,pw:browser", "debug-pw")]
    public async Task The_process_refuses_to_start_under_a_playwright_debug_switch(string variable, string value, string reason)
    {
        using TempBundle bundle = new();
        CapturedLines lines = new();
        int exit = await SittingBrowserServer.RunAsync(bundle.Path, lines,
            _ => throw new InvalidOperationException("a refused start started a stack."), new Dictionary<string, string?> { [variable] = value }, Ct);

        Assert.Equal(1, exit);
        Assert.Matches(Line("refused", "-", $"reason={reason}"), Assert.Single(lines.Lines));
        Assert.False(File.Exists(Path.Combine(bundle.Path, SittingBrowserServer.SocketName)), "a refused start left a socket.");
    }

    [Fact]
    public void A_debug_channel_that_is_not_playwrights_does_not_refuse_a_start()
    {
        Assert.Null(SittingBrowserServer.RefusalOf(new Dictionary<string, string?> { ["DEBUG"] = "express:*" }));
        Assert.Null(SittingBrowserServer.RefusalOf(NoEnvironment));
    }

    [Theory]
    [InlineData("PWDEBUG", "1", "pwdebug")]
    [InlineData("DEBUG", "pw:api", "debug-pw")]
    public async Task The_executable_refuses_to_start_with_pwdebug_set_and_with_debug_pw_api(string variable, string value, string reason)
    {
        using TempBundle bundle = new();
        ChildResult child = await RunExecutableAsync(Start(bundle), atReady: null, TimeSpan.FromMinutes(1), new Dictionary<string, string> { [variable] = value });

        Assert.Equal(1, child.ExitCode);
        Assert.Matches(Line("refused", "-", $"reason={reason}"), child.Output.TrimEnd('\n'));
    }

    [Theory]
    [InlineData("open main")]
    [InlineData("login main --app app1 --as mallory")]
    [InlineData("api main orders/whoami?x=1")]
    [InlineData("watch main --every 0s")]
    [InlineData("hold main")]
    [InlineData("login main --app app1 --password s3cr3t")]
    public async Task A_usage_error_exits_2_before_any_connection_and_echoes_nothing(string command)
    {
        using TempBundle bundle = new();
        ChildResult child = await RunClientAsync(bundle, command.Split(' '));

        Assert.Equal(2, child.ExitCode);
        Assert.Empty(child.Output);
        Assert.DoesNotContain("s3cr3t", child.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_executable_starts_answers_a_client_and_stops_on_stop_and_exits_0()
    {
        using TempBundle bundle = new();
        ChildResult child = await RunExecutableAsync(Start(bundle), atReady: async _ =>
        {
            ChildResult status = await RunClientAsync(bundle, ["status"]);
            Assert.Equal(0, status.ExitCode);
            Assert.Matches(Line("status", "-", "profiles=0"), status.Output.TrimEnd('\n'));
            Assert.Equal(0, (await RunClientAsync(bundle, ["stop"])).ExitCode);
        }, ReadyBound);

        Assert.True(child.ExitCode == 0, $"exit {child.ExitCode}; standard error: {child.Error}");
        string[] lines = [.. child.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Matches(Line("ready", "-", "stack=local"), lines[0]);
        Assert.Matches(Line("stopped", "-", "reason=stop"), lines[^1]);
        Assert.Equal(2, lines.Length);
        Assert.False(File.Exists(Path.Combine(bundle.Path, SittingBrowserServer.SocketName)), "the stop left its socket.");
    }

    [Fact]
    public async Task The_executable_stops_on_sigterm_through_its_own_token_and_exits_0()
    {
        // kill(1) sends SIGTERM by name on macOS and Linux, the machines the suite runs on; Process.Kill sends SIGKILL.
        using TempBundle bundle = new();
        ChildResult child = await RunExecutableAsync(Start(bundle), atReady: async process =>
        {
            using Process kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await kill.WaitForExitAsync(Ct);
            Assert.Equal(0, kill.ExitCode);
        }, ReadyBound);

        Assert.True(child.ExitCode == 0, $"exit {child.ExitCode}; standard error: {child.Error}");
        string[] lines = [.. child.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Matches(Line("stopped", "-", $"reason={SittingBrowserServer.CancelledReason}"), lines[^1]);
    }

    /// <summary>One sample of every shape <see cref="Observations"/> prints, with the values a sitting would see: a 32-hex instance, Cognito's paths, an SSM refusal.</summary>
    private static IEnumerable<(string Member, string Line)> LineShapes()
    {
        DateTimeOffset at = new(2026, 11, 4, 10, 0, 10, TimeSpan.Zero);
        Readiness ready = new("12", "4f1c2b7ad9e84a6bb03e5f17c2a9d8e4");
        yield return (nameof(Observations.SignedIn), Observations.SignedIn(at, "main", "app1", "alice", ready));
        yield return (nameof(Observations.SignedIn), Observations.SignedIn(at, "fresh", "app2", null, ready));
        yield return (nameof(Observations.LoginPrompt), Observations.LoginPrompt(at, "main", "app1", Readiness.Unknown));
        yield return (nameof(Observations.NotFound), Observations.NotFound(at, "old", "app1", ready));
        yield return (nameof(Observations.TunnelDown), Observations.TunnelDown(at, "main", "app1"));
        yield return (nameof(Observations.Timeout), Observations.Timeout(at, "main", "app1"));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "provider", "/oauth2/authorize", Readiness.Unknown, null));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "app1", "/", ready, "session: 500"));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "elsewhere", "/x", Readiness.Unknown, null));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "app1", "/signin-oidc", ready, null, new Answered(400, null, null)));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "app1", "/signin-oidc", ready, null, new Answered(403, "origin_rejected", "The request gate refused the callback")));
        yield return (nameof(Observations.Other), Observations.Other(at, "main", "app1", "app1", "/signin-oidc", Readiness.Unknown, null, Answered.Unknown));
        yield return (nameof(Observations.Marker), Observations.Marker(at, "main", "app1", "Fixture SPA app1"));
        yield return (nameof(Observations.Session), Observations.Session(at, "main", "app1", 401, "unauthenticated"));
        yield return (nameof(Observations.Session), Observations.Session(at, "main", "app1", 401, "session_ended"));
        yield return (nameof(Observations.Session), Observations.Session(at, "main", "app1", 200, null));
        yield return (nameof(Observations.Api), Observations.Api(at, "old", "app1", "/api/orders/whoami", 200, null));
        yield return (nameof(Observations.ClearProvider), Observations.ClearProvider(at, "main", "app1", 3));
        yield return (nameof(Observations.Status), Observations.Status(at, "main", "app1", Observations.LoginPromptToken, at));
        yield return (nameof(Observations.Status), Observations.Status(at, "fresh", "app2", null, null));
        yield return (nameof(Observations.NoProfiles), Observations.NoProfiles(at));
        yield return (nameof(Observations.Closed), Observations.Closed(at, "main", "app1"));
        yield return (nameof(Observations.Unwatched), Observations.Unwatched(at, "main", "app1", 42));
        yield return (nameof(Observations.Ready), Observations.Ready(at, "deployed"));
        yield return (nameof(Observations.Stopped), Observations.Stopped(at, "cancelled"));
        yield return (nameof(Observations.Refused), Observations.Refused(at, "running"));
        yield return (nameof(Observations.NotRunning), Observations.NotRunning(at, "main"));
        yield return (nameof(Observations.Failed), Observations.Failed(at, "main", "marker", "unknown-profile"));
        yield return (nameof(Observations.Failed), Observations.Failed(at, "main", "login", "aws",
            "aws ssm exited 254: An error occurred (AccessDeniedException) when calling the GetParameter operation: not authorized to perform ssm:GetParameter"));
    }

    private static async Task<(int Exit, string Output)> RedactAsync(string[] arguments, string input)
    {
        ProcessStartInfo start = new("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in (string[])["-I", Path.Combine(FixtureFiles.RepositoryRoot, "tools", "wiz_redact.py"), .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(Ct);
        Task<string> error = process.StandardError.ReadToEndAsync(Ct);
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode is 0 or 3, $"wiz_redact.py exited {process.ExitCode}: {await error}");
        return (process.ExitCode, await output);
    }

    /// <summary>
    /// The grammar of #1024 Q3: the token, the UTC time to the second, the profile, then <c>key=value</c>
    /// fields whose keys the redactor never elides, and at most one free text, <c>text=</c>, <c>shell=</c> or <c>title=</c>, last.
    /// </summary>
    private static void AssertGrammar(string line)
    {
        Assert.True(ObservationLine.Head(line) is not null, $"not an observation line: {line}");
        string[] words = Whitespace().Split(line);
        foreach (string word in words.Skip(3))
        {
            Match field = FieldPattern().Match(word);
            Assert.True(field.Success, $"not a key=value field: {word} in {line}");
            string key = field.Groups[1].Value;
            Assert.DoesNotContain(key, ObservationLine.ElidedKeys);
            if (key is "text" or "shell" or "title")
            {
                break;
            }
        }

        Assert.DoesNotContain('{', line);
    }

    /// <summary>The resident process in this test process on the switch's stack, at a bundle of its own.</summary>
    private sealed class Running : IAsyncDisposable
    {
        private readonly TempBundle bundle = new();
        private readonly CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly List<string> answers = [];

        public DirectoryInfo Bundle => new(bundle.Path);

        public CapturedLines ServerLines { get; } = new();

        public Task<int> Run { get; private set; } = Task.FromResult(-1);

        public SittingBrowser Engine { get; private set; } = null!;

        /// <summary>Every line it printed, its own and every client's, in order of arrival per source.</summary>
        public IEnumerable<string> Everything
        {
            get
            {
                lock (answers)
                {
                    return [.. ServerLines.Lines, .. answers];
                }
            }
        }

        public static async Task<Running> StartAsync(Func<CancellationToken, Task<IBrowserStack>>? startStack = null)
        {
            SkipUnlessTheSwitchIsUnset();
            Running running = new();
            TaskCompletionSource<SittingBrowser> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Run = SittingBrowserServer.RunAsync(running.bundle.Path, running.ServerLines, startStack ?? Stacks.StartCancellableAsync, NoEnvironment, running.cancel.Token, started.SetResult);
            await running.ServerLines.WaitForAsync("ready", running.Run);
            running.Engine = await started.Task;
            return running;
        }

        public async Task<Answer> SendAsync(params string[] words)
        {
            (SittingCommand? command, string? usage) = SittingCommand.Parse(words);
            Assert.True(command is not null, usage);
            StringWriter output = new();
            int exit = await SittingBrowserClient.SendAsync(bundle.Path, command, words, output, Ct);
            string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lock (answers)
            {
                answers.AddRange(lines);
            }

            return new Answer(exit, lines);
        }

        public async ValueTask DisposeAsync()
        {
            if (!Run.IsCompleted)
            {
                await cancel.CancelAsync();
                await Run.WaitAsync(StopBound, CancellationToken.None);
            }

            cancel.Dispose();
            bundle.Dispose();
        }
    }

    /// <summary>A stack that is another stack, but for a <c>ProviderHost</c> that throws once told to.</summary>
    public class FailingStack : DispatchProxy
    {
        private IBrowserStack inner = null!;

        public Exception? ProviderHostFailure { get; set; }

        public static FailingStack Over(IBrowserStack inner)
        {
            IBrowserStack proxy = Create<IBrowserStack, FailingStack>();
            FailingStack failing = (FailingStack)(object)proxy;
            failing.inner = inner;
            return failing;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (ProviderHostFailure is { } failure && targetMethod!.Name == "get_" + nameof(IBrowserStack.ProviderHost))
            {
                throw failure;
            }

            try
            {
                return targetMethod!.Invoke(inner, args);
            }
            catch (TargetInvocationException thrown)
            {
                ExceptionDispatchInfo.Capture(thrown.InnerException!).Throw();
                throw;
            }
        }
    }

    /// <summary>
    /// The engine, handed its commands directly so a fact sets its bound, on a local stack whose logins
    /// meet a provider's login form (<see cref="FormProvider"/>), filled as <c>DeployedStack</c> fills
    /// Cognito's (<see cref="FormFillingStack"/>).
    /// </summary>
    private sealed class Fronted(TempBundle bundle) : IAsyncDisposable
    {
        private readonly List<string> lines = [];

        public required FormProvider Form { get; init; }

        public required SittingBrowser Engine { get; init; }

        /// <summary>Every line every command has printed so far, in order of arrival.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines)
                {
                    return [.. lines];
                }
            }
        }

        public static async Task<Fronted> StartAsync(TimeSpan? bound = null)
        {
            SkipUnlessTheSwitchIsUnset();
            FormProvider form = await FormProvider.StartAsync(Ct);
            try
            {
                LocalStack stack = await LocalStack.StartCancellableAsync(null, Ct);
                stack.LoadBalancer.Detour(form.Detour);
                TempBundle bundle = new();
                IBrowserStack filling = FormFillingStack.Over(stack);
                SittingBrowser engine = bound is { } given ? new(filling, bundle.Path, Ct) { Bound = given } : new(filling, bundle.Path, Ct);
                return new Fronted(bundle) { Form = form, Engine = engine };
            }
            catch
            {
                await form.DisposeAsync();
                throw;
            }
        }

        public async Task<Answer> SendAsync(params string[] words)
        {
            (SittingCommand? command, string? usage) = SittingCommand.Parse(words);
            Assert.True(command is not null, usage);
            List<string> answer = [];
            int exit = await Engine.AnswerAsync(command, line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                    answer.Add(line);
                }

                return Task.CompletedTask;
            });
            lock (lines)
            {
                return new Answer(exit, [.. answer]);
            }
        }

        /// <summary>Waits, for no longer than a watch may take to get there, until the lines so far are <paramref name="reached"/>.</summary>
        public async Task WaitForAsync(Func<IReadOnlyList<string>, bool> reached, string what)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (!reached(Lines))
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(40), $"the lines never held {what}: {string.Join(" | ", Lines)}");
                await Task.Delay(100, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Form.DisposeAsync();
            bundle.Dispose();
        }
    }

    /// <summary>
    /// A stack that is another stack, but for the login it completes: at the provider's form it fills
    /// the fields, clicks the submit and returns, as <c>DeployedStack</c>'s form filler does on the cell,
    /// where the local stack has nothing to do.
    /// </summary>
    public class FormFillingStack : DispatchProxy
    {
        private IBrowserStack inner = null!;

        public static IBrowserStack Over(IBrowserStack inner)
        {
            IBrowserStack proxy = Create<IBrowserStack, FormFillingStack>();
            ((FormFillingStack)(object)proxy).inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IBrowserStack.CompleteLoginCancellableAsync))
            {
                return FillAsync(((Profile)args![0]!).Page);
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException thrown)
            {
                ExceptionDispatchInfo.Capture(thrown.InnerException!).Throw();
                throw;
            }
        }

        private async Task FillAsync(IPage page)
        {
            if (new Uri(page.Url).Host != inner.ProviderHost)
            {
                return;
            }

            await page.Locator("input[name='username']:visible").First.FillAsync("alice");
            await page.Locator("input[name='password']:visible").First.FillAsync("not-a-password");
            await page.Locator("button[type='submit']:visible").First.ClickAsync();
        }
    }

    /// <summary>A client call's lines and exit code.</summary>
    private sealed record Answer(int Exit, string[] Lines)
    {
        /// <summary>Exits <paramref name="exit"/> with one line that matches <paramref name="line"/>, which is the last of its lines.</summary>
        public void Assert(int exit, Regex line)
        {
            Xunit.Assert.True(exit == Exit, $"exit {Exit}, not {exit}: {string.Join(" | ", Lines)}");
            Xunit.Assert.NotEmpty(Lines);
            Xunit.Assert.Matches(line, Lines[^1]);
        }
    }

    /// <summary>A bundle directory of its own, short enough for a Unix socket's path (at most 103 bytes on macOS), removed at the end.</summary>
    private sealed class TempBundle : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("sb-");

        public string Path => directory.FullName;

        public void Dispose()
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A browser file still closing; the temporary directory is the system's to clear.
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    private static string[] Start(TempBundle bundle) => [Program.SittingBrowserCommand, Program.BundleOption, bundle.Path, SittingCommand.Start];

    private static Task<ChildResult> RunClientAsync(TempBundle bundle, string[] words) =>
        RunExecutableAsync([Program.SittingBrowserCommand, Program.BundleOption, bundle.Path, .. words], atReady: null, TimeSpan.FromMinutes(2));

    /// <summary>
    /// Skips the calling in-process fact (Assert.Skip) unless the switch names the local stack. #1032 states
    /// these facts with <c>SPA_E2E_STACK</c> unset, and a sitting's suite run sets it to <c>deployed</c>
    /// (the wiz skill's C2 command): there the ready line names the cell and an app the cell does not
    /// name is its <see cref="InvalidOperationException"/>, so the facts would fail on what they do not
    /// prove. The cell's own proof is the first sitting's (#1027). The executable facts unset the switch
    /// in their child, so they run there as here.
    /// </summary>
    private static void SkipUnlessTheSwitchIsUnset()
    {
        if (StackSwitch.FromEnvironment() != StackKind.Local)
        {
            Assert.Skip($"{StackSwitch.Variable} names the deployed cell: the in-process sitting-browser facts are stated with it unset (#1032), and the cell's proof is #1027's.");
        }
    }

    /// <summary>The fixture shell's <c>#marker</c> heading, read from the fixture (fixtures/spa).</summary>
    private static string FixtureMarker(string appId)
    {
        string html = Encoding.UTF8.GetString(FixtureFiles.Spa(appId)["index.html"]);
        Match marker = MarkerHeading().Match(html);
        Assert.True(marker.Success, $"fixtures/spa/{appId}/index.html has no #marker heading.");
        return marker.Groups[1].Value;
    }

    /// <summary>The API the fixture shell calls, its <c>data-api</c> (fixtures/spa).</summary>
    private static string FixtureApi(string appId)
    {
        string html = Encoding.UTF8.GetString(FixtureFiles.Spa(appId)["index.html"]);
        Match api = DataApi().Match(html);
        Assert.True(api.Success, $"fixtures/spa/{appId}/index.html names no data-api.");
        return api.Groups[1].Value;
    }

    private const string TimePattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z";

    /// <summary>An observation line: the token padded as #1024's examples are, the UTC time to the second, the profile, then <paramref name="fields"/> as a pattern.</summary>
    private static Regex Line(string token, string profile, string fields) =>
        new($"^{token} +{TimePattern} {Regex.Escape(profile)}{(fields.Length > 0 ? " " + fields : "")}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The suite's own executable, as <c>ProcessCommandRunner</c> starts a command, with the stack switch
    /// unset and <paramref name="environment"/> set. With <paramref name="atReady"/> set, the action runs
    /// once a ready line appears; standard input is closed at once either way.
    /// </summary>
    private static async Task<ChildResult> RunExecutableAsync(string[] arguments, Func<Process, Task>? atReady, TimeSpan bound, IReadOnlyDictionary<string, string>? environment = null)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Gateway.E2ETests.exe" : "Gateway.E2ETests");
        Assert.True(File.Exists(executable), $"the suite's executable is not at {executable}.");
        ProcessStartInfo start = new(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(StackSwitch.Variable);
        start.Environment.Remove("PWDEBUG");
        start.Environment.Remove("DEBUG");
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(bound);
        StringBuilder output = new();
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                output.Append(line).Append('\n');
                if (atReady is not null && line.StartsWith("ready ", StringComparison.Ordinal))
                {
                    await atReady(process);
                }
            }

            await process.WaitForExitAsync(timeout.Token);
            return new ChildResult(process.ExitCode, output.ToString(), await error);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"{Path.GetFileName(executable)} {string.Join(' ', arguments)} did not finish within {bound}; it printed: {output}");
        }
    }

    [GeneratedRegex("""<h1 id="marker">([^<]+)</h1>""", RegexOptions.None, 1000)]
    private static partial Regex MarkerHeading();

    [GeneratedRegex("data-api=\"([a-z0-9-]+)\"", RegexOptions.None, 1000)]
    private static partial Regex DataApi();

    /// <summary>A scheme, <c>://</c>, then a query somewhere before the next whitespace or quote.</summary>
    [GeneratedRegex("""[A-Za-z][A-Za-z0-9+.-]*://[^\s"']*\?""", RegexOptions.None, 1000)]
    private static partial Regex UrlWithQuery();

    /// <summary>A cookie header with a value, or the gateway's session cookie's name followed by one.</summary>
    [GeneratedRegex("""(?i)(?:^|\s)(?:set-)?cookie:\s*[^\s=;]+=[^\s;]+|__Host-spa[^\s=]*=[^\s;"]+""", RegexOptions.None, 1000)]
    private static partial Regex CookieWithValue();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex("ticks=([0-9]+)", RegexOptions.None, 1000)]
    private static partial Regex TicksField();

    [GeneratedRegex("^([a-z][a-z-]*)=(.+)$", RegexOptions.None, 1000)]
    private static partial Regex FieldPattern();

    private sealed record ChildResult(int ExitCode, string Output, string Error);

    /// <summary>What a client printed, by line, with when each arrived.</summary>
    private sealed class TimedLines : TextWriter
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly StringBuilder pending = new();
        private readonly List<(string, TimeSpan)> lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public IReadOnlyList<(string Line, TimeSpan At)> Lines
        {
            get
            {
                lock (lines)
                {
                    return [.. lines];
                }
            }
        }

        public override void Write(char value)
        {
            lock (lines)
            {
                if (value == '\n')
                {
                    lines.Add((pending.ToString(), clock.Elapsed));
                    pending.Clear();
                }
                else if (value != '\r')
                {
                    pending.Append(value);
                }
            }
        }

        public async Task WaitForCountAsync(int count, Task watch)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (Lines.Count < count)
            {
                if (watch.IsCompleted)
                {
                    Assert.Fail($"the watch ended after {Lines.Count} lines, exit {await (Task<int>)watch}: {string.Join(" | ", Lines.Select(line => line.Line))}");
                }

                Assert.True(waited.Elapsed < TimeSpan.FromMinutes(1), $"{Lines.Count} watch lines within a minute.");
                await Task.Delay(100, Ct);
            }
        }
    }

    /// <summary>What the process wrote on its own output, by line, readable while it runs.</summary>
    private sealed class CapturedLines : TextWriter
    {
        private readonly StringBuilder text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (text)
            {
                text.Append(value);
            }
        }

        public string Text
        {
            get
            {
                lock (text)
                {
                    return text.ToString();
                }
            }
        }

        public string[] Lines => Text.Split(NewLine, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Waits for a line starting with <paramref name="token"/>; fails if the run ends first or none comes within two minutes.</summary>
        public async Task WaitForAsync(string token, Task run)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (!Lines.Any(line => line.StartsWith($"{token} ", StringComparison.Ordinal)))
            {
                if (run.IsCompleted)
                {
                    await run;
                    Assert.Fail($"the sitting browser stopped before a {token} line; it printed: {Text}");
                }

                Assert.True(waited.Elapsed < TimeSpan.FromMinutes(2), $"no {token} line within two minutes; it printed: {Text}");
                await Task.Delay(50, Ct);
            }
        }
    }
}
