using System.Diagnostics;
using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed stack's way of stopping a login at its callback (#211), proven on the local stack's
/// browser, where the stand-in load balancer can say what reached it: the cell has no stand-in to hold
/// a request, so the browser itself must.
/// </summary>
public sealed class NavigationHoldTests
{
    [Fact]
    public async Task A_held_callback_never_leaves_the_browser_and_the_login_it_stopped_can_still_finish()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();

        // The callback is the third hop of a redirect chain that starts at /bff/login and passes the
        // provider, which a Playwright route would not see.
        Uri held = await NavigationHold.HoldAsync(profile.Page, callback, () => profile.GotoAsync(app.Url("/bff/login").ToString()));

        // Asserted without printing the code or the state (Redacted).
        Assert.Equal(callback, Redacted.Url(held));
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(held.Query);
        Assert.True(query.ContainsKey("code") && query.ContainsKey("state"), "the held callback carries no code or no state");
        Assert.Equal(NavigationHold.HeldTitle, await profile.Page.TitleAsync());
        Assert.DoesNotContain(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc");
        Assert.Equal(2, (await profile.CookiesOfAsync(app)).Count);

        // Released, the same callback is a login: the code was not spent, and the browser's later
        // requests are its own again, Fetch Metadata and all.
        await profile.GotoAsync(held.ToString());
        await profile.WaitSignedInAsync(app);
    }

    [Fact]
    public async Task A_hold_that_nothing_reaches_fails_naming_the_url_it_waited_for()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");

        HoldTimeoutException missed = await Assert.ThrowsAsync<HoldTimeoutException>(() =>
            NavigationHold.HoldAsync(profile.Page, app.Url("/signin-oidc").ToString(), () => profile.GotoAsync(app.Url($"/healthz?state={State}").ToString()), TimeSpan.FromSeconds(2)));
        Assert.Contains("/signin-oidc", missed.Message, StringComparison.Ordinal);

        // Called directly, not through Redacted.PlaywrightAsync, the hold's own record of the page has no query (#1003).
        Assert.Equal(app.Url("/healthz").ToString(), missed.Page.Url);
        Assert.DoesNotContain(State, missed.Message, StringComparison.Ordinal);

        // And the page is not left held: the next callback-shaped request reaches the gateway.
        IResponse? passed = await profile.GotoAsync(app.Url("/signin-oidc").ToString());
        Assert.NotNull(passed);
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc");
    }

    // ---- a callback that follows the provider's form, and a handler that fails (#1029) ----

    /// <summary>
    /// A provider's login form, as Cognito's managed login is one: a user name, a password and a submit
    /// button, posted to the provider's own <c>/login</c>.
    /// </summary>
    private const string LoginForm = """
        <!doctype html><title>Signin</title>
        <form method="post" action="/login">
          <input name="username"><input name="password" type="password"><button type="submit">Sign in</button>
        </form>
        """;

    [Fact]
    public async Task A_callback_reached_by_the_redirect_that_answers_the_providers_login_form_is_held_within_the_wait()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();

        // A real callback, its code unspent and its login-state cookies in the browser, which the load
        // balancer stops: the provider's form below answers with a redirect to it, as Cognito's does.
        Uri issued = await profile.StopAtCallbackAsync(app);

        // The form is on another site, as the provider is, and its submission is a top-level POST there
        // that a redirect answers: the shape the deployed rows 5 and 7 meet, which /bff/login's chain of
        // GET redirects is not.
        Uri login = new(ForeignSites.Elsewhere, "/login");
        stack.LoadBalancer.ServeForeignForm(login, LoginForm, issued);
        Stopwatch watch = Stopwatch.StartNew();
        Uri held = await NavigationHold.HoldAsync(profile.Page, callback, async () =>
        {
            await profile.GotoAsync(login.ToString());
            await profile.Page.Locator("input[name='username']").FillAsync("alice");
            await profile.Page.Locator("input[name='password']").FillAsync("not-a-password");
            await profile.Page.Locator("button[type='submit']").ClickAsync();
        }, HeldWait);
        watch.Stop();

        // Compared without printing either URL: both carry the code and the state (Redacted).
        Assert.True(held.AbsoluteUri == issued.AbsoluteUri, $"the hold reported {Redacted.Url(held)}, not the callback the form's answer named");
        Assert.True(watch.Elapsed < HeldWait, $"the hold took {watch.Elapsed}");
        Assert.Equal([login.AbsolutePath], stack.LoadBalancer.Submitted.Select(url => url.AbsolutePath));
        Assert.Equal(NavigationHold.HeldTitle, await profile.Page.TitleAsync());
        Assert.DoesNotContain(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc");

        // Released, the same callback is a login: the hold spent nothing the form's redirect carried.
        await profile.GotoAsync(held.ToString());
        await profile.WaitSignedInAsync(app);
    }

    // ---- a login through the driver's step, whose provider's page submits by script (#1193) ----

    /// <summary>
    /// A provider's login form whose submit is its script's: a POST from the page to another origin, and
    /// then a navigation to the URL that answers it.
    /// </summary>
    private static string ScriptedLoginForm(Uri endpoint) => $$"""
        <!doctype html><title>Signin</title>
        <form>
          <input name="username"><input name="password" type="password"><button type="submit">Sign in</button>
        </form>
        <script>
          document.querySelector("form").addEventListener("submit", async submitted => {
            submitted.preventDefault();
            const answer = await fetch("{{endpoint}}", { method: "POST", body: new URLSearchParams(new FormData(submitted.target)) });
            location.assign(await answer.text());
          });
        </script>
        """;

    [Fact]
    public async Task A_login_through_the_drivers_steps_is_held_within_the_wait_when_the_providers_script_submits_the_form_to_another_origin()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();

        // A real callback, as in the case above, which the provider's endpoint names once the script has
        // posted the form to it. The page is on one foreign origin and the endpoint on another, and the
        // endpoint's preflight allows no header of the test's.
        Uri issued = await profile.StopAtCallbackAsync(app);
        Uri login = new(ForeignSites.Elsewhere, "/login");
        Uri endpoint = new(((IBrowserStack)stack).Sibling, "/submit");
        stack.LoadBalancer.ServeForeign(login, ScriptedLoginForm(endpoint));
        stack.LoadBalancer.AnswerForeignScript(endpoint, issued);

        // Rows 5 and 7: the driver's two steps, with the hold the cell's stack stops the first one at.
        Stopwatch watch = Stopwatch.StartNew();
        Uri? held = null;
        (Step start, Step finish) = await ContinuityDriver.LoginAsync(profile, app, async () => held = await Redacted.PlaywrightAsync(() =>
            NavigationHold.HoldAsync(profile.Page, callback, async () =>
            {
                await profile.GotoAsync(login.ToString());
                await profile.Page.Locator("input[name='username']").FillAsync("alice");
                await profile.Page.Locator("input[name='password']").FillAsync("not-a-password");
                await profile.Page.Locator("button[type='submit']").ClickAsync();
            }, HeldWait)));
        watch.Stop();

        Assert.True(held!.AbsoluteUri == issued.AbsoluteUri, $"the hold reported {Redacted.Url(held)}, not the callback the provider's endpoint named");
        Assert.True(watch.Elapsed < HeldWait, $"the login took {watch.Elapsed}");
        Assert.Equal([endpoint.AbsolutePath], stack.LoadBalancer.Submitted.Select(url => url.AbsolutePath));

        // The callback step is the login's: the gateway answered it, on a request carrying the step's trace.
        Assert.Equal(302, finish.Status);
        Assert.Contains(stack.LoadBalancer.Exchanges, exchange => exchange.Url.AbsolutePath == "/signin-oidc" && exchange.Sent(ClientTrace.Header)?.Contains(finish.TraceId, StringComparison.Ordinal) == true);
        Assert.NotEqual(start.TraceId, finish.TraceId);
    }

    /// <summary>
    /// What the handler met is the hold's failure, whatever its kind: a <see cref="TimeoutException"/>
    /// it met is not the hold's wait running out.
    /// </summary>
    [Theory]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(TimeoutException))]
    public async Task A_handler_that_fails_ends_the_hold_at_once_naming_what_it_met_and_printing_no_code_or_state(string kind)
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();

        Stopwatch watch = Stopwatch.StartNew();
        HoldFailedException failed = await Assert.ThrowsAsync<HoldFailedException>(() =>
            NavigationHold.HoldAsync(profile.Page, callback, () => profile.GotoAsync(app.Url("/bff/login").ToString()), NavigationHold.DefaultWait, FailingAnswer(callback, kind)));
        watch.Stop();

        Assert.True(watch.Elapsed < AtOnce, $"the hold ended after {watch.Elapsed}, not at once: its wait was {NavigationHold.DefaultWait}");
        Assert.Equal(kind, failed.Failure);
        Assert.Contains($"its handler met {kind}: the answer failed at {callback}", failed.Message, StringComparison.Ordinal);
        Assert.False(failed.Message.Contains(Code, StringComparison.Ordinal), "the failure's message holds the code canary");
        Assert.False(failed.Message.Contains(State, StringComparison.Ordinal), "the failure's message holds the state canary");
    }

    [Fact]
    public async Task A_failed_handler_is_written_into_the_rows_evidence_with_what_it_met_and_what_the_page_showed()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string callback = app.Url("/signin-oidc").ToString();
        await profile.OnOriginOfAsync(app);

        string directory = Directory.CreateTempSubdirectory("spa-e2e-hold-").FullName;
        try
        {
            ContinuityRun run = await RunInAsync(directory);
            Stopwatch watch = Stopwatch.StartNew();

            // As rows 5 and 7 reach the hold, inside Redacted.PlaywrightAsync (Profile.StopAtCallbackAsync).
            await Assert.ThrowsAsync<HoldFailedException>(() => run.TwoTaskRowAsync("row5", new Step("control", ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 200), null,
                async (n, _) =>
                {
                    await Redacted.PlaywrightAsync(() => NavigationHold.HoldAsync(profile.Page, callback,
                        () => profile.GotoAsync(app.Url("/bff/login").ToString()), NavigationHold.DefaultWait, FailingAnswer(callback)));
                    return new Attempt(n, []);
                },
                (_, _) => RowResult.Passed("never judged", []),
                Ct));
            watch.Stop();

            string file = await File.ReadAllTextAsync(Path.Combine(directory, "row5.json"), Ct);
            RowEvidence written = JsonSerializer.Deserialize<RowEvidence>(file)!;
            Assert.True(watch.Elapsed < AtOnce, $"the row stopped after {watch.Elapsed}");
            Assert.Equal(Verdict.NotProved, written.Verdict);
            Assert.Equal($"the driver stopped with {nameof(HoldFailedException)}: the hold's handler met {nameof(InvalidOperationException)}", written.Reason);
            Assert.Equal(app.Url("/healthz").ToString(), written.Page?.Url);
            foreach ((string name, string value) in new[] { ("code", Code), ("state", State) })
            {
                Assert.False(file.Contains(value, StringComparison.Ordinal), $"the evidence file holds the {name} canary");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>How long a hold that met a provider's form may take: the form, its redirect and the answer, on loopback.</summary>
    private static readonly TimeSpan HeldWait = TimeSpan.FromSeconds(20);

    /// <summary>Well inside <see cref="NavigationHold.DefaultWait"/>, with room for the page's text (<see cref="NavigationHold.TextWait"/>).</summary>
    private static readonly TimeSpan AtOnce = TimeSpan.FromSeconds(20);

    /// <summary>
    /// An answer that throws what the hold's handler once let escape, none of the three kinds it caught:
    /// an <see cref="InvalidOperationException"/> unless <paramref name="kind"/> names a
    /// <see cref="TimeoutException"/>. Its message names a callback with a code and a state, as a
    /// protocol failure's might.
    /// </summary>
    private static Func<ICDPSession, JsonElement, Task> FailingAnswer(string callback, string kind = nameof(InvalidOperationException))
    {
        string message = $"the answer failed at {callback}?code={Code}&state={State}";
        return (_, _) => throw (kind == nameof(TimeoutException) ? new TimeoutException(message) : new InvalidOperationException(message));
    }

    private static async Task<ContinuityRun> RunInAsync(string directory)
    {
        RecordedRunner runner = RecordedRunner.WithExits(command => command switch
        {
            { Program: "git", Arguments: [.., "check-ignore", "-q", "--", _] } => new CommandResult(0, ""),
            _ => throw new InvalidOperationException($"unexpected {command.Program}"),
        });
        return new ContinuityRun(new ContinuityEvidence(directory, runner), new ContinuityLog(
            Cell.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json"), Ct)), runner));
    }

    // ---- what a timed-out hold leaves in a continuity row's evidence file (#1003) ----

    private const string Code = "CODE-CANARY-0123456789";
    private const string State = "STATE-CANARY-0123456789";

    /// <summary>A JWT-shaped token, which no rule but its shape could know: it is in no URL and no cookie.</summary>
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0b2tlbi1jYW5hcnkifQ.dG9rZW4tY2FuYXJ5LXNpZ25hdHVyZQ";

    /// <summary>The fragment's ID token, a second JWT, and a cookie value the page echoes.</summary>
    private const string IdToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJpZC1jYW5hcnkifQ.aWQtY2FuYXJ5LXNpZ25hdHVyZQ";
    private const string CookieValue = "COOKIE-CANARY-0123456789";

    [Fact]
    public async Task A_timed_out_hold_writes_the_pages_query_less_url_and_visible_text_to_the_evidence_file_and_no_token_code_or_state()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        Uri callback = app.Url("/signin-oidc");

        // A provider's page that stops the login: its URL carries a code and a state and its fragment an
        // ID token; its text echoes the state, a callback URL with the code, a token and a cookie value; a
        // hidden field and a script hold the token where no visible text does.
        Uri shown = new(((IBrowserStack)stack).Sibling, $"/login?code={Code}&state={State}#id_token={IdToken}");
        string html = $$"""
            <!doctype html><title>Signin</title>
            <form><p>Sign in with your username and password</p><input type="hidden" name="_csrf" value="{{Token}}"></form>
            <p>An error was encountered with the requested page: state {{State}} did not match.</p>
            <p>Continue at {{callback}}?code={{Code}}&amp;state={{State}}</p>
            <p>Your token is {{Token}} and your session is {{CookieValue}}.</p>
            <script>window.token = "{{Token}}";</script>
            """;
        await profile.Context.AddCookiesAsync([new Cookie { Name = "provider-session", Value = CookieValue, Url = shown.GetLeftPart(UriPartial.Authority) }]);

        string directory = Directory.CreateTempSubdirectory("spa-e2e-hold-").FullName;
        try
        {
            RecordedRunner runner = RecordedRunner.WithExits(command => command switch
            {
                { Program: "git", Arguments: [.., "check-ignore", "-q", "--", _] } => new CommandResult(0, ""),
                _ => throw new InvalidOperationException($"unexpected {command.Program}"),
            });
            ContinuityRun run = new(new ContinuityEvidence(directory, runner), new ContinuityLog(
                Cell.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureFiles.RepositoryRoot, "contracts", "infra-outputs.example.json"), Ct)), runner));

            // Rows 5 and 7 reach the hold through Profile.StopAtCallbackAsync, which runs it inside
            // Redacted.PlaywrightAsync: so does this one.
            await Assert.ThrowsAsync<HoldTimeoutException>(() => run.TwoTaskRowAsync("row5", new Step("control", ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 200), null,
                async (n, _) =>
                {
                    await Redacted.PlaywrightAsync(() => NavigationHold.HoldAsync(profile.Page, callback.ToString(),
                        () => profile.OpenForeignPageAsync(shown, html), TimeSpan.FromSeconds(2)));
                    return new Attempt(n, []);
                },
                (_, _) => RowResult.Passed("never judged", []),
                Ct));

            string file = await File.ReadAllTextAsync(Path.Combine(directory, "row5.json"), Ct);
            PageShown page = JsonSerializer.Deserialize<RowEvidence>(file)!.Page!;
            Assert.Equal(shown.GetLeftPart(UriPartial.Path), page.Url);
            Assert.Contains("Sign in with your username and password", page.Text, StringComparison.Ordinal);
            Assert.Contains($"Continue at {callback}{Environment.NewLine}", page.Text + Environment.NewLine, StringComparison.Ordinal);
            Assert.Contains("An error was encountered with the requested page: state", page.Text, StringComparison.Ordinal);
            foreach ((string name, string value) in new[] { ("code", Code), ("state", State), ("token", Token), ("ID token", IdToken), ("cookie", CookieValue) })
            {
                // Says which, never prints the file: the file is what must hold none of them.
                Assert.False(file.Contains(value, StringComparison.Ordinal), $"the evidence file holds the {name} canary");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_timed_out_hold_writes_the_requests_that_failed_and_the_console_errors_to_the_evidence_file_and_no_token_code_or_state()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        Uri callback = app.Url("/signin-oidc");

        // A provider's page that shows no error and whose script meets three: an endpoint on another
        // origin that refuses its preflight, the same endpoint's 405, and an error it logs. Each carries
        // a code, a state or a token, in a query or in the text.
        Uri shown = new(ForeignSites.Elsewhere, $"/login?code={Code}&state={State}");
        Uri endpoint = new(((IBrowserStack)stack).Sibling, "/submit");
        stack.LoadBalancer.AnswerForeignScript(endpoint, callback);
        string html = $$"""
            <!doctype html><title>Signin</title>
            <form><p>Sign in</p></form>
            <script>
              console.error("the session {{CookieValue}} met state {{State}} with {{Token}} at {{callback}}?code={{Code}}");
              fetch("{{endpoint}}?state={{State}}", { method: "POST", headers: { "x-provider": "1" } }).catch(() => {});
              fetch("{{endpoint}}?code={{Code}}").catch(() => {});
            </script>
            """;
        await profile.Context.AddCookiesAsync([new Cookie { Name = "provider-session", Value = CookieValue, Url = shown.GetLeftPart(UriPartial.Authority) }]);

        string directory = Directory.CreateTempSubdirectory("spa-e2e-hold-").FullName;
        try
        {
            ContinuityRun run = await RunInAsync(directory);
            await Assert.ThrowsAsync<HoldTimeoutException>(() => run.TwoTaskRowAsync("row5", new Step("control", ClientTrace.Fresh().TraceId, DateTimeOffset.UtcNow, 200), null,
                async (n, _) =>
                {
                    await Redacted.PlaywrightAsync(() => NavigationHold.HoldAsync(profile.Page, callback.ToString(),
                        () => profile.OpenForeignPageAsync(shown, html), TimeSpan.FromSeconds(3)));
                    return new Attempt(n, []);
                },
                (_, _) => RowResult.Passed("never judged", []),
                Ct));

            string file = await File.ReadAllTextAsync(Path.Combine(directory, "row5.json"), Ct);
            PageShown page = JsonSerializer.Deserialize<RowEvidence>(file)!.Page!;
            Assert.Contains($"POST {endpoint} failed: net::ERR_FAILED", page.Requests!);
            Assert.Contains($"GET {endpoint} 405", page.Requests!);
            Assert.Contains(page.Errors!, line => line.StartsWith("the session ", StringComparison.Ordinal) && line.Contains($" at {callback}", StringComparison.Ordinal));

            // The browser's own report of the refused preflight, which names the header it refused.
            Assert.Contains(page.Errors!, line => line.Contains("x-provider", StringComparison.Ordinal) && line.Contains(endpoint.ToString(), StringComparison.Ordinal));
            foreach ((string name, string value) in new[] { ("code", Code), ("state", State), ("token", Token), ("cookie", CookieValue) })
            {
                // Says which, never prints the file: the file is what must hold none of them.
                Assert.False(file.Contains(value, StringComparison.Ordinal), $"the evidence file holds the {name} canary");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_page_whose_text_cannot_be_read_is_recorded_as_such_and_not_as_a_new_failure()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.GotoAsync(app.Url($"/healthz?state={State}").ToString());
        await profile.Page.CloseAsync();

        PageShown page = await NavigationHold.ShownAsync(profile.Page);

        Assert.Equal(app.Url("/healthz").ToString(), page.Url);
        Assert.StartsWith("(the page's text could not be read within", page.Text, StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
