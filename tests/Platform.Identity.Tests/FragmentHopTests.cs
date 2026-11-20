using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// A fragment's hop and the merge's writes (#994; the fragments spec #961 -> "Failing hops and the
/// marks" and its traces comment's W1, W2 and W4 rows): a definitive refusal marks the fragment
/// <c>unavailable</c>, anything else writes nothing, the chain goes on, and each bounded write logs its
/// <c>store_attempts</c>. Each test names the row it proves.
/// </summary>
public sealed class FragmentHopTests
{
    private static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    private static readonly FragmentDefinition Billing = FragmentChainTests.Billing;
    private static readonly AppDefinition Shell = FragmentChainTests.Shell(Orders, Billing);

    private const string Start = "/bff/login?returnUrl=%2Fhome";

    private static async Task<(IdentityGateway Gateway, ScriptedSessionStore Store, CapturedLog Log)> StartAsync(
        bool stubProfile = true, ClientCredentialStyle style = ClientCredentialStyle.PostBody)
    {
        ScriptedSessionStore store = new(new InMemorySessionStore());
        CapturedLog log = new();
        IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell],
            services => services.AddSingleton(log.Provider),
            stubProfile: stubProfile,
            sessions: new RecordingSessionStore(store));
        gateway.Profile.ClientCredentialStyle = style;
        return (gateway, store, log);
    }

    private static async Task<SessionRecord> RecordAsync(IdentityGateway gateway) => (await gateway.Sessions.SingleAsync()).Record;

    /// <summary>The fragment-write event lines: writer, outcome and store_attempts, in order.</summary>
    private static (string Writer, string Outcome, int Attempts, string Fragment)[] Writes(CapturedLog log) =>
    [
        .. log.Lines
            .Where(l => l.Category == typeof(FragmentEvents).FullName && l.Level == LogLevel.Information)
            .Select(l => (
                (string)l.State.Single(s => s.Key == "Writer").Value!,
                (string)l.State.Single(s => s.Key == "Outcome").Value!,
                (int)l.State.Single(s => s.Key == "StoreAttempts").Value!,
                (string)l.State.Single(s => s.Key == "FragmentId").Value!)),
    ];

    [Theory]
    [InlineData(ClientCredentialStyle.PostBody)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader)]
    public async Task W1_a_definitive_refusal_marks_the_fragment_unavailable_and_the_chain_continues_to_returnUrl(ClientCredentialStyle style)
    {
        (IdentityGateway gateway, _, CapturedLog log) = await StartAsync(style: style);
        await using IdentityGateway owned = gateway;
        // The provider holds another secret for the orders client than the gateway's: invalid_client at
        // the code exchange, in either credential style.
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal(302, end.Status);
        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
        Assert.Contains(("mark", "written", 1, "orders"), Writes(log));
        Assert.Contains(browser.Answers, a => a.Rule == RuleIds.AuthCallback && a.Status == 302);
    }

    [Fact]
    public async Task W1_a_login_required_hop_writes_nothing_leaves_the_fragment_loginRequired_and_the_chain_continues()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync(stubProfile: false);
        await using IdentityGateway owned = gateway;
        // The fidelity switch (#997): prompt=none with no provider session answers login_required.
        gateway.Idp.Faults.ModelProviderSession = true;
        using FragmentBrowser browser = new(gateway, Shell) { KeepProviderSession = false };

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Billing));
        Assert.Equal(0, store.Replaces);
        Assert.Empty(Writes(log));
        Assert.Equal(2, gateway.Idp.Authorizations.Count(a => a.Outcome == TestIdp.Authorization.LoginRequired));
    }

    [Fact]
    public async Task W1_a_transient_provider_failure_writes_nothing_and_the_chain_continues()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        // After the shell's own login, the provider signs with a key it never published: the fragments'
        // ID tokens fail validation, a failure that carries no OAuth error code.
        browser.BeforeAuthorize = uri =>
        {
            gateway.Idp.Faults.SignWithUnpublishedKey = FragmentSchemeTests.Query(uri)["client_id"] != Shell.OAuth.ClientId;
            return Task.CompletedTask;
        };

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Billing));
        Assert.Equal(0, store.Replaces);
    }

    [Fact]
    public async Task A_set_issued_for_another_sub_is_refused_and_marks_the_fragment_unavailable()
    {
        (IdentityGateway gateway, _, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        browser.BeforeAuthorize = uri =>
        {
            gateway.Idp.DefaultSubject = FragmentSchemeTests.Query(uri)["client_id"] == Orders.OAuth.ClientId ? "mallory" : "alice";
            return Task.CompletedTask;
        };

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal("alice", record.Subject);
        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
        Assert.DoesNotContain(Held.Properties(record).Items.Keys, k => k.StartsWith(".Token.", StringComparison.Ordinal) && k.EndsWith(":orders", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("shell")]
    public async Task A_foreign_or_garbage_state_at_a_fragment_callback_answers_the_failure_page_and_leaves_the_record_unchanged(string kind)
    {
        (IdentityGateway gateway, ScriptedSessionStore store, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        SessionRecord before = await RecordAsync(gateway);
        int replaces = store.Replaces;

        // A state the shell's scheme minted is protected under another purpose than the fragment's.
        string state = "garbage";
        if (kind == "shell")
        {
            IdentityGateway.Answer challenge = await gateway.SendAsync(Shell, "/", headers: IdentityGateway.Navigation);
            state = challenge.RedirectQuery["state"];
        }

        IdentityGateway.Answer answer = await browser.SendAsync("/signin-oidc/orders?code=code-x&state=" + Uri.EscapeDataString(state), null, new Dictionary<string, string>());

        Assert.Equal(400, answer.Status);
        Assert.Equal(AuthenticationEvents.FailurePage, answer.Body);
        Assert.Equal(RuleIds.AuthCallback, answer.Rule);
        SessionRecord after = await RecordAsync(gateway);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Ticket.ToArray(), after.Ticket.ToArray());
        Assert.Equal(replaces, store.Replaces);
    }

    [Fact]
    public async Task W1_a_conflicting_merge_re_reads_and_re_applies_and_the_event_line_counts_the_retried_write()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        // Another writer wins the first replace: a concurrent refresh of the shell's set, say.
        store.BeforeReplace = async (key, attempt) =>
        {
            if (attempt == 1)
            {
                await store.BumpAsync(key);
            }
        };
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
        Assert.Equal([("chain", "written", 2, "orders"), ("chain", "written", 1, "billing")], Writes(log));

    }

    [Fact]
    public async Task W1_a_conflict_that_exhausts_MergeBudget_keeps_the_winner_writes_no_mark_warns_and_the_chain_continues()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        // Every replace for the orders hop loses; billing's are left alone.
        bool losing = true;
        store.BeforeReplace = async (key, _) =>
        {
            if (losing)
            {
                await store.BumpAsync(key);
            }
        };
        using FragmentBrowser browser = new(gateway, Shell);
        browser.BeforeAuthorize = uri =>
        {
            losing = FragmentSchemeTests.Query(uri)["client_id"] != Billing.OAuth.ClientId;
            return Task.CompletedTask;
        };

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(record, Billing));
        Assert.Contains(("chain", "exhausted", 3, "orders"), Writes(log));
        Assert.Contains(log.Lines, l => l.Category == typeof(FragmentEvents).FullName && l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task W1_a_missing_record_writes_nothing_and_stops_the_chain_at_returnUrl()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        browser.BeforeAuthorize = async uri =>
        {
            if (FragmentSchemeTests.Query(uri)["client_id"] == Orders.OAuth.ClientId)
            {
                await store.Inner.RemoveAsync(gateway.Sessions.Created.Single(), CancellationToken.None);
            }
        };

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        Assert.Null(await store.Inner.GetAsync(gateway.Sessions.Created.Single(), CancellationToken.None));
        Assert.Equal(0, store.Replaces);
        Assert.Equal([("chain", "missing", 0, "orders")], Writes(log));
        // The chain stopped: billing's hop never started.
        Assert.DoesNotContain(gateway.Idp.Authorizations, a => a.ClientId == Billing.OAuth.ClientId);
    }

    [Fact]
    public async Task A_null_re_read_after_a_lost_replace_writes_nothing_and_resurrects_nothing()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        store.BeforeReplace = (key, _) => store.Inner.RemoveAsync(key, CancellationToken.None);
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        Assert.Null(await store.Inner.GetAsync(gateway.Sessions.Created.Single(), CancellationToken.None));
        Assert.Equal(1, store.Replaces);
        Assert.Equal([("chain", "missing", 1, "orders")], Writes(log));
    }

    [Theory]
    [InlineData(false, FragmentSets.LoginRequired)]
    [InlineData(true, FragmentSets.SignedIn)]
    public async Task W1_an_unknown_write_answers_503_stops_the_chain_and_the_fragment_reads_as_committed(bool committed, string reads)
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        if (committed)
        {
            store.AfterReplace = _ => throw ScriptedSessionStore.Unavailable("replace (PutItem)");
        }
        else
        {
            store.BeforeReplace = (_, _) => throw ScriptedSessionStore.Unavailable("replace (PutItem)");
        }

        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal(503, end.Status);
        Assert.Equal(RuleIds.AuthStore, end.Rule);
        using (JsonDocument problem = JsonDocument.Parse(end.Body))
        {
            Assert.Equal(Problems.SessionStoreUnavailable, problem.RootElement.GetProperty("code").GetString());
        }

        Assert.DoesNotContain(end.Cookies, c => c.Name.Value == CookieOptionsSetup.CookieName);
        SessionRecord record = await RecordAsync(gateway);
        Assert.Equal(reads, Held.StateOf(record, Orders));
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Billing));
        Assert.Equal([("chain", "store_unavailable", 1, "orders")], Writes(log));
    }

    [Fact]
    public async Task W2_the_fallback_after_a_provider_refusal_marks_unavailable_and_redirects_to_returnUrl()
    {
        (IdentityGateway gateway, _, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        gateway.Idp.ResetLog();

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback");

        // The fallback discards nothing, so the orders set it held is not overwritten by the mark.
        Assert.Equal("/back", end.Location);
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(await RecordAsync(gateway), Orders));
        Assert.Contains(("mark", "unchanged", 0, "orders"), Writes(log));
        Assert.DoesNotContain(gateway.Idp.Authorizations, a => a.ClientId == Shell.OAuth.ClientId);
    }

    [Fact]
    public async Task W2_the_fallback_for_a_fragment_never_signed_in_marks_it_unavailable_on_a_definitive_refusal()
    {
        (IdentityGateway gateway, _, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(await RecordAsync(gateway), Orders));

        // Still refused: the mark stands; the provider fixed: the fallback replaces it with a set.
        Assert.Equal("/back", (await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback")).Location);
        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(await RecordAsync(gateway), Orders));
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, IdentityGateway.SecretOf(Orders));
        Assert.Equal("/back", (await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback")).Location);
        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(await RecordAsync(gateway), Orders));
    }

    [Fact]
    public async Task W2_a_conflict_that_exhausts_MergeBudget_redirects_to_returnUrl_with_the_state_unchanged()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, IdentityGateway.SecretOf(Orders));
        store.BeforeReplace = (key, _) => store.BumpAsync(key);

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback");

        Assert.Equal(302, end.Status);
        Assert.Equal("/back", end.Location);
        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(await RecordAsync(gateway), Orders));
        Assert.Contains(("fallback", "exhausted", 3, "orders"), Writes(log));
    }

    [Fact]
    public async Task W2_a_missing_record_redirects_to_returnUrl_and_an_unknown_write_answers_503()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        using FragmentBrowser browser = new(gateway, Shell);
        await browser.NavigateAsync(Start);
        string key = gateway.Sessions.Created.Single();

        store.BeforeReplace = (_, _) => throw ScriptedSessionStore.Unavailable("replace (PutItem)");
        IdentityGateway.Answer unknown = await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback");
        Assert.Equal(503, unknown.Status);
        Assert.Equal(RuleIds.AuthStore, unknown.Rule);

        store.BeforeReplace = null;
        browser.BeforeAuthorize = _ => store.Inner.RemoveAsync(key, CancellationToken.None);
        IdentityGateway.Answer missing = await browser.NavigateAsync("/bff/login?fragment=orders&returnUrl=%2Fback");
        Assert.Equal(302, missing.Status);
        Assert.Equal("/back", missing.Location);
        Assert.Null(await store.Inner.GetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task W4_a_mark_re_applies_on_a_conflict_within_MergeBudget()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        store.BeforeReplace = async (key, attempt) =>
        {
            if (attempt <= 2)
            {
                await store.BumpAsync(key);
            }
        };
        using FragmentBrowser browser = new(gateway, Shell);

        await browser.NavigateAsync(Start);

        Assert.Equal(FragmentSets.Unavailable, Held.StateOf(await RecordAsync(gateway), Orders));
        Assert.Contains(("mark", "written", 3, "orders"), Writes(log));
    }

    [Fact]
    public async Task W4_a_mark_that_exhausts_MergeBudget_writes_no_mark()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        gateway.Idp.SetClientSecret(Billing.OAuth.ClientId, "rotated-elsewhere");
        store.BeforeReplace = (key, _) => store.BumpAsync(key);
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(await RecordAsync(gateway), Orders));
        Assert.Equal([("mark", "exhausted", 3, "orders"), ("mark", "exhausted", 3, "billing")], Writes(log));
    }

    [Fact]
    public async Task W4_a_mark_on_a_missing_record_writes_nothing_and_an_unknown_one_answers_503()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, _) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        store.BeforeReplace = (_, _) => throw ScriptedSessionStore.Unavailable("replace (PutItem)");
        using FragmentBrowser unknownBrowser = new(gateway, Shell);
        IdentityGateway.Answer unknown = await unknownBrowser.NavigateAsync(Start);
        Assert.Equal(503, unknown.Status);
        Assert.Equal(RuleIds.AuthStore, unknown.Rule);

        store.BeforeReplace = null;
        using FragmentBrowser browser = new(gateway, Shell);
        browser.BeforeAuthorize = async uri =>
        {
            if (FragmentSchemeTests.Query(uri)["client_id"] == Orders.OAuth.ClientId)
            {
                await store.Inner.RemoveAsync(gateway.Sessions.Created[^1], CancellationToken.None);
            }
        };

        IdentityGateway.Answer missing = await browser.NavigateAsync(Start);
        Assert.Equal("/home", missing.Location);
        Assert.Null(await store.Inner.GetAsync(gateway.Sessions.Created[^1], CancellationToken.None));
    }

    [Fact]
    public async Task A_definitive_refusal_never_overwrites_a_set_signed_in_meanwhile()
    {
        (IdentityGateway gateway, ScriptedSessionStore store, CapturedLog log) = await StartAsync();
        await using IdentityGateway owned = gateway;
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);
        // Between the refused hop's start and its mark, another writer signs the orders set in.
        SessionRecord? signedIn = null;
        store.BeforeReplace = async (key, attempt) =>
        {
            if (attempt == 1 && signedIn is null)
            {
                SessionRecord current = (await store.Inner.GetAsync(key, CancellationToken.None))!;
                Microsoft.AspNetCore.Authentication.AuthenticationTicket ticket =
                    Microsoft.AspNetCore.Authentication.TicketSerializer.Default.Deserialize(current.Ticket.ToArray())!;
                FragmentSets.WriteSet(ticket.Properties, Orders, [new() { Name = "access_token", Value = "elsewhere" }]);
                signedIn = current with { Version = current.Version + 1, Ticket = Microsoft.AspNetCore.Authentication.TicketSerializer.Default.Serialize(ticket) };
                Assert.True(await store.Inner.ReplaceAsync(key, signedIn, current.Version, CancellationToken.None));
            }
        };

        await browser.NavigateAsync(Start);

        Assert.Equal(FragmentSets.SignedIn, Held.StateOf(await RecordAsync(gateway), Orders));
        Assert.Contains(("mark", "unchanged", 1, "orders"), Writes(log));
    }
}
