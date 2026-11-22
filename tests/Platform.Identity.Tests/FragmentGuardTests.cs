using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Guards of the sign-in chain the review of #994 found unproven: no log line carries the session key,
/// a continuation or a token (N-16); the merge's budget is <see cref="PlatformPolicy.MergeBudget"/>, not
/// its placeholder; a callback an earlier event already ended is neither signed in nor merged, as the
/// stock handler keeps it; and a shell's failed create answers 503 before any hop.
/// </summary>
public sealed class FragmentGuardTests
{
    private static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    private static readonly FragmentDefinition Billing = FragmentChainTests.Billing;
    private static readonly AppDefinition Shell = FragmentChainTests.Shell(Orders, Billing);

    private const string Start = "/bff/login?returnUrl=%2Fhome";

    [Fact]
    public async Task No_log_line_of_a_chain_carries_the_session_key_a_continuation_or_a_token()
    {
        // Mutation: log hop.Continuation.SessionKey beside the writer in FragmentEvents.WriteAsync's
        // event line, and this test fails.
        CapturedLog log = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], services => services.AddSingleton(log.Provider));
        // A definitive refusal on one hop, so the mark's line and the refused exchange's are searched too.
        gateway.Idp.SetClientSecret(Orders.OAuth.ClientId, "rotated-elsewhere");
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal("/home", end.Location);
        (string key, SessionRecord record) = await gateway.Sessions.SingleAsync();
        AuthenticationProperties held = Held.Properties(record);
        string[] tokens =
        [
            .. held.Items
                .Where(i => (i.Key.StartsWith(".Token.access_token", StringComparison.Ordinal)
                        || i.Key.StartsWith(".Token.refresh_token", StringComparison.Ordinal)
                        || i.Key.StartsWith(".Token.id_token", StringComparison.Ordinal))
                    && !string.IsNullOrEmpty(i.Value))
                .Select(i => i.Value!),
        ];
        string[] continuations =
        [
            .. browser.Redirects
                .Where(r => r.Contains("/bff/chain?c=", StringComparison.Ordinal))
                .Select(r => Uri.UnescapeDataString(r[(r.IndexOf("?c=", StringComparison.Ordinal) + 3)..])),
        ];

        // The canaries are there to find: the shell's three tokens and billing's two, and a continuation per step.
        Assert.True(tokens.Length >= 5, $"{tokens.Length} tokens held");
        Assert.True(continuations.Length >= 2, $"{continuations.Length} continuations");
        string[] lines = [.. log.Lines.Select(l => l.Text)];
        Assert.Contains(lines, l => l.Contains("versioned replaces", StringComparison.Ordinal));
        foreach (string canary in (string[])[key, .. tokens, .. continuations])
        {
            Assert.DoesNotContain(lines, l => l.Contains(canary, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_merge_budget_is_the_policys_MergeBudget()
    {
        // Mutation: Bounded reads DefaultBudget whatever the policy says, and the write ends after 3.
        ScriptedSessionStore store = new(new InMemorySessionStore());
        CapturedLog log = new();
        PlatformPolicy policy = new([], [], ImmutableDictionary<string, IssuerDefinition>.Empty) { MergeBudget = 2 };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell],
            services => services.AddSingleton(log.Provider).AddSingleton(policy),
            sessions: new RecordingSessionStore(store));
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
        Assert.Contains(("chain", "exhausted", 2, "orders"), Writes(log));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("orders")]
    public async Task A_callback_an_earlier_event_ended_is_neither_signed_in_nor_merged(string endedFor)
    {
        // The stock handler signs in only when the ticket event left the response unhandled and the
        // request not skipped (RemoteAuthenticationHandler.HandleRequestAsync:
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs).
        // Mutation: drop the Result check in FragmentEvents.ApplyShell, and the shell row creates a
        // session; drop it in ApplyFragment, and the orders row merges its set.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell]);
        string client = endedFor == "shell" ? Shell.OAuth.ClientId : Orders.OAuth.ClientId;
        gateway.Profile.OnConfigure = (options, app, _) =>
        {
            if (app.OAuth.ClientId == client)
            {
                options.Events.OnTicketReceived = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status418ImATeapot;
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            }
        };
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal(StatusCodes.Status418ImATeapot, end.Status);
        if (endedFor == "shell")
        {
            Assert.Empty(gateway.Sessions.Created);
            Assert.DoesNotContain(gateway.Idp.Authorizations, a => a.ClientId == Orders.OAuth.ClientId);
        }
        else
        {
            SessionRecord record = (await gateway.Sessions.SingleAsync()).Record;
            Assert.Equal(FragmentSets.LoginRequired, Held.StateOf(record, Orders));
        }
    }

    [Fact]
    public async Task A_failed_create_on_a_shells_login_callback_answers_503_and_starts_no_hop()
    {
        // The shell's callback signs in inside the ticket event, which the remote handler raises outside
        // its own try, so the store's failure reaches PlatformOidcHandler as the stock sign-in's does.
        ScriptedSessionStore store = new(new InMemorySessionStore())
        {
            BeforeCreate = () => throw ScriptedSessionStore.Unavailable("create (PutItem)"),
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([Shell], sessions: new RecordingSessionStore(store));
        using FragmentBrowser browser = new(gateway, Shell);

        IdentityGateway.Answer end = await browser.NavigateAsync(Start);

        Assert.Equal((503, RuleIds.AuthStore), (end.Status, end.Rule));
        Assert.DoesNotContain(gateway.Idp.Authorizations, a => a.ClientId == Orders.OAuth.ClientId);
    }

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
}
