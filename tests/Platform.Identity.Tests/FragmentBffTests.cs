using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The fragment parts of <c>/bff/*</c> (#994; contracts/http-contract.md -> Endpoints and Fragments):
/// the <c>/bff/chain</c> step's refusals and skips (ADR-0040), <c>GET /bff/session</c>'s
/// <c>fragments</c>, the fallback <c>GET /bff/login?fragment=</c>, and <c>GET /bff/fragments</c>.
/// The steps are sent with synthetic Fetch Metadata: a <c>cross-site</c> navigation, as a step reached
/// through the provider's redirect arrives.
/// </summary>
public sealed class FragmentBffTests
{
    private static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    private static readonly FragmentDefinition Billing = FragmentChainTests.Billing;
    private static readonly AppDefinition Shell = FragmentChainTests.Shell(Orders, Billing);
    private static readonly AppDefinition Other = TestApps.Entry("other", issuer: IdentityGateway.IssuerName);

    private const string Start = "/bff/login?returnUrl=%2Fhome";

    /// <summary>The shell's login up to its first <c>/bff/chain</c> step, unsent: the browser holds the session.</summary>
    private static async Task<(FragmentBrowser Browser, string Step)> FirstStepAsync(IdentityGateway gateway)
    {
        FragmentBrowser browser = new(gateway, Shell) { StopAtChain = true };
        IdentityGateway.Answer step = await browser.NavigateAsync(Start);
        Assert.StartsWith("/bff/chain?c=", step.Location, StringComparison.Ordinal);
        browser.StopAtChain = false;
        return (browser, step.Location);
    }

    private static string Mint(IdentityGateway gateway, string appId, ChainContinuation continuation) =>
        "/bff/chain?c=" + continuation.Protect(gateway.Services.GetRequiredService<IDataProtectionProvider>(), appId);

    private static Task<IdentityGateway.Answer> StepAsync(FragmentBrowser browser, string step) =>
        browser.SendAsync(step, FragmentBrowser.CrossSiteNavigation, new Dictionary<string, string>());

    [Fact]
    public async Task A_cross_site_step_with_a_valid_continuation_challenges_the_next_fragment()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;

        IdentityGateway.Answer answer = await StepAsync(browser, step);

        Assert.Equal(302, answer.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, answer.Location, StringComparison.Ordinal);
        Assert.Equal(Orders.OAuth.ClientId, answer.RedirectQuery["client_id"]);
        Assert.Null(answer.Rule);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("other-session")]
    [InlineData("other-app")]
    [InlineData("two-values")]
    public async Task A_refused_continuation_lands_on_root_under_auth_chain_with_the_record_unchanged(string kind)
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell, Other]);
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;
        string key = gateway.Sessions.Created.Single();
        SessionRecord before = (await gateway.Sessions.GetAsync(key, CancellationToken.None))!;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ChainContinuation valid = new(key, ["orders", "billing"], 0, "/home", now.AddMinutes(5));

        // The control: the same mint, as the step would accept it, proceeds.
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, (await StepAsync(browser, Mint(gateway, Shell.AppId, valid))).Location, StringComparison.Ordinal);

        string refused = kind switch
        {
            "missing" => "/bff/chain",
            "tampered" => step[..^3] + (step[^3] == 'A' ? 'B' : 'A') + step[^2..],
            "expired" => Mint(gateway, Shell.AppId, valid with { ExpiresAt = now.AddSeconds(-1) }),
            "other-session" => Mint(gateway, Shell.AppId, valid with { SessionKey = "another-session-key" }),
            "other-app" => Mint(gateway, Other.AppId, valid),
            "two-values" => step + "&c=" + step["/bff/chain?c=".Length..],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        int authorizations = gateway.Idp.Authorizations.Count;
        IdentityGateway.Answer answer = await StepAsync(browser, refused);

        Assert.Equal(302, answer.Status);
        Assert.Equal("/", answer.Location);
        Assert.Equal(RuleIds.AuthChain, answer.Rule);
        Assert.Equal("", answer.Body);
        Assert.DoesNotContain(answer.Cookies, c => c.Name.Value == CookieOptionsSetup.CookieName);
        SessionRecord after = (await gateway.Sessions.GetAsync(key, CancellationToken.None))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Ticket.ToArray(), after.Ticket.ToArray());
        Assert.Equal(authorizations, gateway.Idp.Authorizations.Count);
    }

    [Fact]
    public async Task Another_sessions_step_is_refused_for_this_browser()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (FragmentBrowser first, string firstStep) = await FirstStepAsync(gateway);
        (FragmentBrowser second, _) = await FirstStepAsync(gateway);
        using FragmentBrowser a = first;
        using FragmentBrowser b = second;

        IdentityGateway.Answer answer = await StepAsync(second, firstStep);

        Assert.Equal("/", answer.Location);
        Assert.Equal(RuleIds.AuthChain, answer.Rule);
    }

    [Fact]
    public async Task A_step_whose_next_fragment_was_removed_skips_it_and_a_fragment_added_mid_chain_is_not_chained()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;
        FragmentDefinition added = TestApps.Fragment("added", "shell");
        AppDefinition next = FragmentChainTests.Shell(Billing, added);
        await gateway.SwapAsync(next);
        using FragmentBrowser onNext = new(gateway, next);
        onNext.Jar.Clear();
        foreach ((string name, string value) in browser.Jar)
        {
            onNext.Jar[name] = value;
        }

        IdentityGateway.Answer first = await onNext.SendAsync(step, FragmentBrowser.CrossSiteNavigation, new Dictionary<string, string>());
        Assert.Equal(Billing.OAuth.ClientId, first.RedirectQuery["client_id"]);

        IdentityGateway.Answer end = await onNext.NavigateAsync(step, FragmentBrowser.CrossSiteNavigation);
        Assert.Equal("/home", end.Location);
        SessionRecord record = (await gateway.Sessions.SingleAsync()).Record;
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, added));
        Assert.DoesNotContain(gateway.Idp.Authorizations, a => a.ClientId == Orders.OAuth.ClientId || a.ClientId == added.OAuth.ClientId);
    }

    [Fact]
    public async Task A_replayed_step_does_no_work_twice()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;
        Assert.Equal("/home", (await browser.NavigateAsync(step, FragmentBrowser.CrossSiteNavigation)).Location);
        SessionRecord before = (await gateway.Sessions.SingleAsync()).Record;
        int authorizations = gateway.Idp.Authorizations.Count;

        IdentityGateway.Answer replayed = await StepAsync(browser, step);

        Assert.Equal(302, replayed.Status);
        Assert.Equal("/home", replayed.Location);
        Assert.Null(replayed.Rule);
        Assert.Equal(authorizations, gateway.Idp.Authorizations.Count);
        Assert.Equal(before.Version, (await gateway.Sessions.SingleAsync()).Record.Version);
    }

    [Fact]
    public async Task A_step_with_no_session_gets_the_shell_gates_challenge()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;
        browser.Jar.Clear();

        IdentityGateway.Answer answer = await StepAsync(browser, step);

        Assert.Equal(302, answer.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, answer.Location, StringComparison.Ordinal);
        Assert.Equal(Shell.OAuth.ClientId, answer.RedirectQuery["client_id"]);
        Assert.Null(answer.Rule);
    }

    [Fact]
    public async Task A_store_failure_inside_a_step_answers_503()
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], sessions: new RecordingSessionStore(store));
        (FragmentBrowser browser, string step) = await FirstStepAsync(gateway);
        using FragmentBrowser owned = browser;
        store.BeforeGet = _ => throw ScriptedSessionStore.Unavailable("read (GetItem)");

        IdentityGateway.Answer answer = await StepAsync(browser, step);

        Assert.Equal(503, answer.Status);
        Assert.Equal(RuleIds.AuthStore, answer.Rule);
        using JsonDocument problem = JsonDocument.Parse(answer.Body);
        Assert.Equal(Problems.SessionStoreUnavailable, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bff_session_reports_every_fragment_of_the_current_entry()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        gateway.Idp.SetClientSecret(Billing.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);

        Dictionary<string, string> states = await FragmentsOfSessionAsync(gateway, Shell, browser.Jar);
        Assert.Equal(new Dictionary<string, string> { ["orders"] = "signedIn", ["billing"] = "unavailable" }, states);

        // A fragment onboarded after this login reads loginRequired, and one moved onto a new client
        // reads its old set as absent.
        FragmentDefinition added = TestApps.Fragment("added", "shell");
        FragmentDefinition moved = TestApps.Fragment("orders", "shell", clientId: "client-shell-orders-2",
            clientSecretRef: "ssm:/spa-platform/poc/apps/shell/fragments/orders/client-secret:2");
        AppDefinition next = FragmentChainTests.Shell(moved, Billing, added);
        await gateway.SwapAsync(next);

        Assert.Equal(
            new Dictionary<string, string> { ["orders"] = "loginRequired", ["billing"] = "unavailable", ["added"] = "loginRequired" },
            await FragmentsOfSessionAsync(gateway, next, browser.Jar));
    }

    [Fact]
    public async Task Bff_session_reports_an_empty_object_for_a_shell_with_no_fragments()
    {
        AppDefinition plain = TestApps.Entry("plain", issuer: IdentityGateway.IssuerName);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([plain]);
        IdentityGateway.Login login = await gateway.LoginAsync(plain);

        IdentityGateway.Answer session = await gateway.SendAsync(plain, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);

        using JsonDocument body = JsonDocument.Parse(session.Body);
        JsonElement fragments = body.RootElement.GetProperty("fragments");
        Assert.Equal(JsonValueKind.Object, fragments.ValueKind);
        Assert.Empty(fragments.EnumerateObject());
    }

    [Fact]
    public async Task The_fallback_replaces_a_set_without_discarding_the_session()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        (string key, SessionRecord before) = await gateway.Sessions.SingleAsync();
        string ordersBefore = Held.Properties(before).Items[FragmentSets.TokenItem("access_token", "orders")]!;
        string shellBefore = Held.Properties(before).Items[".Token.access_token"]!;

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback");

        Assert.Equal(302, end.Status);
        Assert.Equal("/back", end.Location);
        (string sameKey, SessionRecord after) = await gateway.Sessions.SingleAsync();
        Assert.Equal(key, sameKey);
        Assert.Empty(gateway.Sessions.Removed);
        Assert.NotEqual(ordersBefore, Held.Properties(after).Items[FragmentSets.TokenItem("access_token", "orders")]);
        Assert.Equal(shellBefore, Held.Properties(after).Items[".Token.access_token"]);
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(after, Billing));
        // The old set was replaced without being revoked.
        Assert.Empty(gateway.Idp.Revocations);
    }

    [Fact]
    public async Task The_fallback_with_no_session_is_the_full_login_chain_included()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?fragment=billing&returnUrl=%2Fback");

        Assert.Equal("/back", end.Location);
        SessionRecord record = (await gateway.Sessions.SingleAsync()).Record;
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task The_fallback_for_an_unknown_fragment_redirects_to_returnUrl_unchanged(string fragment)
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        SessionRecord before = (await gateway.Sessions.SingleAsync()).Record;
        int authorizations = gateway.Idp.Authorizations.Count;

        IdentityGateway.Answer answer = await browser.SendAsync(
            "/bff/login?fragment=" + fragment + "&returnUrl=%2Fback", IdentityGateway.Navigation, new Dictionary<string, string>());

        Assert.Equal(302, answer.Status);
        Assert.Equal("/back", answer.Location);
        Assert.Equal(authorizations, gateway.Idp.Authorizations.Count);
        Assert.Equal(before.Version, (await gateway.Sessions.SingleAsync()).Record.Version);
    }

    [Fact]
    public async Task The_fallback_answers_503_on_a_store_failure()
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], sessions: new RecordingSessionStore(store));
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        store.BeforeGet = _ => throw ScriptedSessionStore.Unavailable("read (GetItem)");

        IdentityGateway.Answer answer = await browser.SendAsync(
            "/bff/login?fragment=orders&returnUrl=%2Fback", IdentityGateway.Navigation, new Dictionary<string, string>());

        Assert.Equal(503, answer.Status);
        Assert.Equal(RuleIds.AuthStore, answer.Rule);
    }

    [Fact]
    public async Task Bff_fragments_lists_each_fragment_and_its_entry_url_uncached()
    {
        FragmentDefinition custom = TestApps.Fragment("custom", "shell", entry: "main.mjs.js");
        AppDefinition shell = FragmentChainTests.Shell(Orders, custom);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        using FragmentBrowser browser = new(gateway, shell);
        await browser.NavigateAsync(Start);

        IdentityGateway.Answer answer = await gateway.SendAsync(shell, "/bff/fragments", browser.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(200, answer.Status);
        Assert.Equal("no-store", answer.CacheControl);
        using JsonDocument body = JsonDocument.Parse(answer.Body);
        (string, string)[] items =
        [
            .. body.RootElement.GetProperty("fragments").EnumerateArray()
                .Select(f => (f.GetProperty("fragmentId").GetString()!, f.GetProperty("entryUrl").GetString()!)),
        ];
        Assert.Equal([("orders", "/fragments/orders/entry.js"), ("custom", "/fragments/custom/main.mjs.js")], items);
    }

    [Fact]
    public async Task Bff_fragments_answers_401_unauthenticated_with_no_session()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);

        IdentityGateway.Answer answer = await gateway.SendAsync(Shell, "/bff/fragments", headers: IdentityGateway.Fetch);

        Assert.Equal(401, answer.Status);
        Assert.Equal("no-store", answer.CacheControl);
        using JsonDocument problem = JsonDocument.Parse(answer.Body);
        Assert.Equal(Problems.Unauthenticated, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<Dictionary<string, string>> FragmentsOfSessionAsync(
        IdentityGateway gateway, AppDefinition app, IReadOnlyDictionary<string, string> jar)
    {
        IdentityGateway.Answer session = await gateway.SendAsync(app, "/bff/session", jar, headers: IdentityGateway.Fetch);
        Assert.Equal(200, session.Status);
        using JsonDocument body = JsonDocument.Parse(session.Body);
        return body.RootElement.GetProperty("fragments").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
