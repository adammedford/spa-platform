using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-16, whole: "Run the full suite with canary values for tokens, secrets, cookies, codes and
/// state, then search every log line -> No canary appears." (docs/plan/04-tests.md.)
///
/// <para><b>Why this row is a suite property and not a test.</b> Its subject is "every log line", so
/// the only honest reading covers lines this class never produced. Two things carry it:
/// <see cref="Suite.StartAsync"/> enrols every harness in the project with
/// <see cref="CanarySweep"/>, and <see cref="SuiteSweep"/> — an assembly fixture, disposed after the
/// last test class — sweeps the whole run's log. The tests below are the part that can be named:
/// they send the five kinds of credential the row lists, and sweep what has been collected by the
/// time they run.</para>
///
/// <para><b>Why the request line is the hazard.</b> <c>contracts/telemetry.md</c> -> Why query
/// strings are dropped: the callback carries the authorization code and state in the query string,
/// ADR-0007 accepts that for Phase 1, and the framework's own request log would include it — so the
/// platform replaces that log, holds the authentication loggers at Information, and keeps
/// <c>Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c>, query strings, tokens, codes, state,
/// client secrets and session keys off every line. This row is the check on all of it at once.</para>
///
/// <para>The sweep covers every enrolled harness in this test assembly, including lines produced
/// outside this class. It does not establish that unexercised login, refresh or logout paths
/// redact credentials: those operations need their own acceptance requests.</para>
/// </summary>
public sealed class CanaryTests(CanaryTests.Cell cell) : IClassFixture<CanaryTests.Cell>
{
    /// <summary>One harness for this class. Its client secrets are canaries by the harness's own design.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    [Fact]
    public async Task No_canary_this_suite_has_sent_appears_in_any_line_it_has_produced()
    {
        // Every kind the row names, each on the request shape where it would do real damage.
        await SendCredentialsAsync();

        // Lines are written after the pipeline returns, so wait for the last of them before
        // sweeping: a sweep that ran first would report an absence it was too early to see.
        await Observed.SettledCountAsync(Harness, Suite.Ct);

        CanarySweep.Assert("after this class sent every kind of credential");
    }

    [Fact]
    public async Task The_request_lines_of_those_requests_exist_and_carry_none_of_it()
    {
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        await SendCredentialsAsync();
        await Observed.UntilAsync(
            () => Observed.RequestLines(Harness).Count > before, "the request lines of this class", Suite.Ct);
        await Observed.SettledCountAsync(Harness, Suite.Ct);

        // The sweep above would also pass if the gateway logged nothing at all, so this names what
        // the lines must contain as well as what they must not: the requests were logged, with a
        // status and a route, and the credentials are not in them
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        IReadOnlyList<System.Text.Json.JsonElement> produced = [.. Observed.RequestLines(Harness).Skip(before)];
        Assert.NotEmpty(produced);
        foreach (System.Text.Json.JsonElement line in produced)
        {
            Assert.NotNull(Observed.Field(line, "route"));
            Assert.NotNull(Observed.Number(line, "status"));
        }

        Assert.Empty(CanarySweep.Leaks(
            produced.Select(line => line.GetRawText()), CanarySweep.Values));
    }

    [Fact]
    public void A_canary_in_a_line_is_found_by_the_sweep()
    {
        // The guard on the guard. Every other assertion in this class is an absence, so the search
        // itself has to be shown to find something — otherwise a sweep that silently matched nothing
        // would report the same clean result forever
        // (CODING_STANDARDS.md -> "A check that is vacuous today").
        string planted = $"{{\"msg\":\"request\",\"authorization\":\"Bearer {Canaries.Token}\"}}";

        IReadOnlyList<string> leaks = CanarySweep.Leaks([planted, "{\"msg\":\"request\"}"], CanarySweep.Values);

        string leak = Assert.Single(leaks);
        Assert.Contains("a bearer token", leak, StringComparison.Ordinal);

        // And the description does not repeat the credential, because this message is itself logged
        // by whatever is running the suite.
        Assert.DoesNotContain(Canaries.Token, leak, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_apps_client_secret_is_among_the_values_swept()
    {
        // The secrets are the one canary kind this suite does not type: the harness mints one per app
        // per run, "so a secret seen in a log line is this run's and no other's (N-16)", and the
        // gateway reads them when it exchanges a code. Collected from the harness rather than
        // restated, so an app added later is swept for without this file changing.
        foreach (HarnessApp app in Harness.Apps.Values)
        {
            Assert.Contains(app.ClientSecret, CanarySweep.Values);
        }

        Assert.NotEmpty(Harness.Apps);
    }

    [Fact]
    public async Task Every_token_a_signed_in_visit_was_issued_is_among_the_values_swept()
    {
        // The real credentials of the session rows: a login and an API call through the real token
        // source, then the provider's own record of what it issued. Swept by value at the end of the
        // run with every other canary, and searched for here over this visit's own lines.
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);
        using TestBrowser browser = harness.CreateBrowser();
        await harness.SignInAsync(browser, "app1", Suite.Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await browser.GetAsync("/api/orders/whoami", ct: Suite.Ct)).StatusCode);

        string[] issued = [.. harness.Idp.IssuedAccessTokens, .. harness.Idp.IssuedRefreshTokens];
        Assert.NotEmpty(harness.Idp.IssuedAccessTokens);
        Assert.NotEmpty(harness.Idp.IssuedRefreshTokens);
        Assert.All(issued, token => Assert.True(CanarySweep.Values.Contains(token), "an issued token is not swept for"));
        Assert.Empty(CanarySweep.Leaks(harness.Log.Lines, issued));
    }

    /// <summary>
    /// One request per kind of credential the row names, each sent where it would matter: the
    /// session cookie's value, a bearer token, the antiforgery header, and a code and state in a
    /// query string on the callback path and on an asset path. Some are refused and two are not, so
    /// both the refusal lines and the ordinary request lines are swept. The two that are not carry a
    /// signed-in browser's session, because an asset is served and an API call forwarded only in one
    /// (ADR-0008), and every answer is asserted, so the stages each comment names are the ones the
    /// credential went through.
    /// </summary>
    private async Task SendCredentialsAsync()
    {
        HarnessApp app = Harness.App("app1");
        using TestBrowser browser = Harness.CreateBrowser();
        await Harness.SignInAsync(browser, app.AppId, Suite.Ct);
        (string, string) session = ("Cookie", SignedIn.CookieHeader(browser, app));

        (string Target, (string, string)[] Headers, int Status)[] requests =
        [
            // The session cookie, under the platform's own name, on an asset request. It names no
            // session, so the cookie handler reads it and the shell gate refuses it under auth.session.
            ("/index.html", [("Cookie", $"{SignedIn.SessionCookieName}={Canaries.Cookie}")], 401),

            // A bearer token and the antiforgery header on an API call that is refused, so the
            // refusal's own Warning line is swept as well as the request line.
            ("/api/echo/thing",
            [
                ("Sec-Fetch-Site", FetchMetadata.CrossSite),
                ("Authorization", $"Bearer {Canaries.Token}"),
                ("X-CSRF-TOKEN", Canaries.Csrf),
            ], 403),

            // The same two on an API call that is not refused, which reaches an upstream.
            ("/api/echo/thing",
            [
                ("Sec-Fetch-Site", FetchMetadata.SameOrigin),
                ("Authorization", $"Bearer {Canaries.Token}"),
                ("X-CSRF-TOKEN", Canaries.Csrf),
                session,
            ], 200),

            // A code and a state in a query string, on the callback path the contract names. No login
            // is in flight, so the callback is refused with the failure page.
            ($"/signin-oidc?code={Canaries.Code}&state={Canaries.State}", [], 400),

            // And on an asset path, where the framework's own request log would have carried them.
            ($"/index.html?code={Canaries.Code}&state={Canaries.State}", [session], 200),

            // And on the app's root with no session, which the shell gate refuses.
            ($"/?code={Canaries.Code}&state={Canaries.State}", [], 401),
        ];

        foreach ((string target, (string, string)[] headers, int status) in requests)
        {
            RawResponse response = await RawHttp.SendAsync(Harness.GatewayAddress, "GET", target, app.Host, headers, Suite.Ct);
            Assert.Equal(status, response.StatusCode);
        }

        // The last one again on an unknown host, so a line with no app_id is swept too.
        RawResponse unknown = await RawHttp.SendAsync(
            Harness.GatewayAddress, "GET", $"/?code={Canaries.Code}&state={Canaries.State}",
            "nobody.spa-poc.test:8443", ct: Suite.Ct);
        Assert.Equal(404, unknown.StatusCode);
    }
}
