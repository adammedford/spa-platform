using System.Net;
using Gateway.SecurityTests.Negative;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Fragments;

/// <summary>
/// <c>GET /bff/chain</c> refused, on the composed gateway (#1007, part of #961 -> "The eager chain";
/// contracts/request-gate.md, the <c>/bff/chain</c> row; contracts/http-contract.md -> Fragments ->
/// "<c>/bff/chain</c>"). Each row stops a real login at its first chain step, so the continuation
/// <c>c</c> it sends, or spoils, is one the shell's callback minted; the browser by then holds the
/// shell's session and no fragment's set.
/// </summary>
public sealed class ChainGateTests
{
    /// <summary>
    /// N-43: a step without <c>Sec-Fetch-Mode: navigate</c> and <c>Sec-Fetch-Dest: document</c> is 403
    /// <c>origin_rejected</c> under <c>gate.navigation</c>; a POST is 405 <c>method_not_allowed</c>
    /// under <c>gate.method</c> with <c>Allow: GET</c>; the record is unchanged.
    /// </summary>
    [Fact]
    public async Task G9_a_chain_step_that_is_not_a_navigation_or_not_a_GET_is_refused()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.Shell(1), Suite.Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        Uri step = await FragmentLogins.UpToFirstStepAsync(harness, browser, shell);
        long version = (await FragmentLogins.RecordAsync(harness, browser, shell)).Version;
        int authorizations = harness.Idp.Authorizations.Count;

        // A fetch (cors, empty), and a navigation into a frame (navigate, iframe): neither is a
        // top-level document navigation.
        Contract.ErrorRow originRejected = Contract.Error("origin_rejected");
        int beforeGate = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse[] notNavigations =
        [
            await browser.GetAsync(step.PathAndQuery, SignedIn.Manual, Suite.Ct),
            await browser.NavigateAsync(step, SignedIn.Manual with { Headers = RequestOptions.With(("Sec-Fetch-Dest", "iframe")).Headers }, Suite.Ct),
            await browser.NavigateAsync(step, SignedIn.Manual with { Headers = RequestOptions.With(("Sec-Fetch-Mode", "no-cors")).Headers }, Suite.Ct),
        ];
        foreach (BrowserResponse response in notNavigations)
        {
            // Mutation that turns it red: dropping /bff/chain from the gate's navigation rows lets each
            // reach the step, which challenges the fragment's scheme with a 302.
            Assert.Equal((HttpStatusCode)originRejected.Status, response.StatusCode);
            Assert.Contains($"\"code\":\"{originRejected.Code}\"", response.Body, StringComparison.Ordinal);
        }

        // One request line each, every one under gate.navigation.
        await Observed.UntilAsync(() => Observed.RequestLines(harness).Count >= beforeGate + 3, "the three refusals' lines", Suite.Ct);
        Assert.All(Observed.RequestLines(harness).Skip(beforeGate), line => Assert.Equal(RuleIds.GateNavigation, Observed.Field(line, "rule")));
        Assert.Equal(beforeGate + 3, Observed.RequestLines(harness).Count);

        // A form POST is a document navigation, so only the method is wrong. Mutation: admitting any
        // method on the /bff/chain row turns this 302 or 405-less.
        int beforePost = await Observed.SettledCountAsync(harness, Suite.Ct);
        BrowserResponse post = await browser.SubmitFormAsync(step.PathAndQuery, options: SignedIn.Manual, ct: Suite.Ct);
        Contract.ErrorRow methodNotAllowed = Contract.Error("method_not_allowed");
        Assert.Equal((HttpStatusCode)methodNotAllowed.Status, post.StatusCode);
        Assert.Contains($"\"code\":\"{methodNotAllowed.Code}\"", post.Body, StringComparison.Ordinal);
        Assert.Equal("GET", post.Header("Allow"));
        Assert.Equal(RuleIds.GateMethod, await Observed.RuleOfOneRequestAsync(harness, beforePost, Suite.Ct));

        // Nothing moved: no provider call and the same record version.
        Assert.Equal(authorizations, harness.Idp.Authorizations.Count);
        Assert.Equal(version, (await FragmentLogins.RecordAsync(harness, browser, shell)).Version);

        // The control: the same continuation, sent as a navigation, still drives the chain to its end,
        // so the refusals above were the gate's and not a spent continuation's.
        BrowserResponse proceeded = await browser.NavigateAsync(step, ct: Suite.Ct);
        Assert.Equal(HttpStatusCode.OK, proceeded.StatusCode);
        Assert.Equal(shell.Url("/"), proceeded.Url);
        Assert.Equal(authorizations + 1, harness.Idp.Authorizations.Count);
    }

    /// <summary>
    /// N-44: a missing, tampered, expired, other-session or other-app continuation each ends with a 302
    /// to <c>/</c>, no problem body, rule <c>auth.chain</c> on the request line and in
    /// <c>security.rejections</c>, no provider call, and the record's version unchanged; the shell's
    /// session still answers 200.
    /// </summary>
    [Fact]
    public async Task G9_a_bad_continuation_lands_on_root_under_auth_chain_with_the_record_unchanged()
    {
        SteppedClock clock = new();
        await using GatewayHarness harness = await Suite.StartAsync(
            GatewayHarnessOptions.Shell(1) with { Apps = ["shell", "app1"], Clock = clock }, Suite.Ct);
        HarnessApp shell = harness.App("shell"), app1 = harness.App("app1");
        using CountedMetrics metrics = CountedMetrics.Watch(harness);

        using TestBrowser browser = harness.CreateBrowser();
        Uri step = await FragmentLogins.UpToFirstStepAsync(harness, browser, shell);
        string continuation = SignedIn.Query(step, "c");

        // Another session's continuation: a second browser's own first step.
        using TestBrowser other = harness.CreateBrowser();
        string othersContinuation = SignedIn.Query(await FragmentLogins.UpToFirstStepAsync(harness, other, shell), "c");

        // The same browser signed in to app 1 too, so an other-app step meets a session on its host.
        await harness.SignInAsync(browser, "app1", Suite.Ct);

        // One character of the protected payload changed, away from either end.
        int at = continuation.Length / 2;
        string tampered = continuation[..at] + (continuation[at] == 'A' ? 'B' : 'A') + continuation[(at + 1)..];

        (string Case, Func<Uri> Url)[] cases =
        [
            ("missing", () => shell.Url("/bff/chain")),
            ("tampered", () => shell.Url("/bff/chain?c=" + tampered)),
            ("other-session", () => shell.Url("/bff/chain?c=" + othersContinuation)),
            ("other-app", () => app1.Url("/bff/chain?c=" + continuation)),
            // Last, because it moves the clock past ChainLifetime (five minutes) for every case after it.
            ("expired", () => { clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); return step; }),
        ];

        foreach ((string name, Func<Uri> url) in cases)
        {
            long version = (await FragmentLogins.RecordAsync(harness, browser, shell)).Version;
            int authorizations = harness.Idp.Authorizations.Count;
            long counted = metrics.Sum("security.rejections", ("rule", RuleIds.AuthChain));
            int before = await Observed.SettledCountAsync(harness, Suite.Ct);
            Uri target = url();

            BrowserResponse response = await browser.NavigateAsync(target, SignedIn.Manual, Suite.Ct);

            // Mutation that turns it red: a step that skips the session-key comparison proceeds on the
            // other-session continuation with a 302 to the provider; one that skips the expiry check
            // does the same on the expired one; one that reads c under no app purpose does so on the
            // other-app one. Each fails the Location assertion, naming its case.
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{name}: {response}");
            Assert.True(response.Header("Location") == "/", $"{name}: Location {response.Header("Location")}");
            Assert.True(response.Body.Length == 0, $"{name}: a problem body {response.Body}");
            Assert.True(await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct) == RuleIds.AuthChain, $"{name}: rule");
            Assert.True(metrics.Sum("security.rejections", ("rule", RuleIds.AuthChain)) == counted + 1, $"{name}: security.rejections");
            Assert.True(harness.Idp.Authorizations.Count == authorizations, $"{name}: a provider call");
            Assert.True((await FragmentLogins.RecordAsync(harness, browser, shell)).Version == version, $"{name}: the record changed");

            // Read from a page on the shell's host: the other-app case left the browser on app 1's.
            await SignedIn.OnPageOfAsync(browser, shell);
            BrowserResponse session = await browser.GetAsync(shell.Url("/bff/session").ToString(), ct: Suite.Ct);
            Assert.True(session.StatusCode == HttpStatusCode.OK, $"{name}: the shell's session answered {session}");
        }

        // The other-app case met app 1's session, not none: app 1's session still answers too.
        await SignedIn.OnPageOfAsync(browser, app1);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(app1.Url("/bff/session").ToString(), ct: Suite.Ct)).StatusCode);
    }

    /// <summary>
    /// N-45: a <c>/bff/chain</c> step with no session gets the shell gate's usual challenge for a
    /// navigation, and no fragment challenge.
    /// </summary>
    [Fact]
    public async Task G9_a_chain_step_with_no_session_gets_the_shell_gate_s_challenge()
    {
        await using GatewayHarness harness = await Suite.StartAsync(GatewayHarnessOptions.Shell(1), Suite.Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser signedIn = harness.CreateBrowser();
        Uri step = await FragmentLogins.UpToFirstStepAsync(harness, signedIn, shell);

        // A browser with no session at all, sending a real continuation.
        using TestBrowser fresh = harness.CreateBrowser();
        BrowserResponse response = await fresh.NavigateAsync(step, SignedIn.Manual, Suite.Ct);

        // Mutation that turns it red: a step that reads the continuation before the session challenges
        // the fragment's scheme, whose redirect_uri is /signin-oidc/orders and client the fragment's.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri authorize = new(response.Header("Location")!);
        Assert.Equal(harness.Idp.AuthorizeEndpoint, authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(shell.ClientId, SignedIn.Query(authorize, "client_id"));
        Assert.Equal(shell.Url("/signin-oidc").ToString(), SignedIn.Query(authorize, "redirect_uri"));
        Assert.DoesNotContain(shell.Fragment("orders").ClientId, response.Header("Location")!, StringComparison.Ordinal);
    }
}
