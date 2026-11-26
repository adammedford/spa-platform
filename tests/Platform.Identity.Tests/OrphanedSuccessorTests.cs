using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// A refresh whose write finds the session gone revokes the successor it obtained (#1544, F-48's
/// behaviour; contracts/http-contract.md -> "Logout", contracts/telemetry.md -> Orphaned successor line),
/// at the token probe's seam against <c>fixtures/TestIdp</c>. The session is removed between the
/// provider's answer and the refresh's versioned replace, as a logout on another task removes it.
/// </summary>
public sealed class OrphanedSuccessorTests
{
    private static readonly FragmentDefinition Orders = FragmentRefreshTests.Orders;
    private static readonly AppDefinition Shell = FragmentRefreshTests.Shell;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ClientCredentialStyle.PostBody, ClientAuth.Post, false)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader, ClientAuth.Basic, false)]
    [InlineData(ClientCredentialStyle.PostBody, ClientAuth.Post, true)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader, ClientAuth.Basic, true)]
    public async Task A_refresh_whose_write_finds_the_session_gone_revokes_its_successor_by_the_sets_own_client(
        ClientCredentialStyle style, ClientAuth expected, bool onFragment)
    {
        // F-48 at the hermetic tier, for the shell's set and a fragment's: the session is removed after the
        // provider issued the successor and before the write, so the write ends missing. The successor is
        // revoked by the set's own client in the profile's credential style, one orphaned successor line
        // reads revoked (with fragment_id on a fragment's set only), and the call answers unauthenticated.
        // Mutation: drop the revocation; the successor stays redeemable, and this fails (4 tests red).
        // Mutation: revoke through the shell's scheme; a fragment's revocation is by the shell's client (2 red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        gateway.Profile.ClientCredentialStyle = style;
        FragmentDefinition? fragment = onFragment ? Orders : null;
        string client = fragment?.OAuth.ClientId ?? Shell.OAuth.ClientId;
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(client));
        store.BeforeReplace = RemoveOnFirst(store);

        IdentityGateway.Answer answer = await FragmentRefreshTests.TokenAsync(gateway, jar, fragment);

        Assert.Equal((401, "unauthenticated"), (answer.Status, answer.Body));
        Assert.Null(await store.GetAsync(key, Ct));
        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal(client, grant.ClientId);
        Revocation revocation = Assert.Single(gateway.Idp.Revocations);
        Assert.Equal((client, expected, grant.RefreshTokenIssued), (revocation.ClientId, revocation.Auth, revocation.Token));
        Assert.False(gateway.Idp.IsRedeemable(grant.RefreshTokenIssued!));

        CapturedLine line = Assert.Single(OrphanLines(logs));
        Assert.Equal(
            fragment is null ? ["AppId", "Outcome", "SessionRef"] : ["AppId", "FragmentId", "Outcome", "SessionRef"],
            Fields(line));
        Assert.Equal("revoked", Field(line, "Outcome"));
        Assert.Equal(SessionRef.Of(key), Field(line, "SessionRef"));
        Assert.Equal(Shell.AppId, Field(line, "AppId"));
        if (fragment is not null)
        {
            Assert.Equal(fragment.FragmentId, Field(line, "FragmentId"));
        }

        string all = string.Join("\n", logs.Lines.Select(l => l.Text));
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
        TokenSearch.AssertNoTokens(gateway.Idp, all);
    }

    [Fact]
    public async Task The_revocation_starts_only_after_the_sets_gate_is_given_up()
    {
        // #1544: a waiter queued on the set's gate behind the refresh is answered while the revocation is
        // still held at the provider, so the gate's hold stays within the refresh write deadline. The
        // waiter has read the session before the removal, so it reaches the gate. Mutation: revoke inside
        // WriteAsync, under the gate; the waiter is held until the revocation ends, and this fails.
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, _) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(Shell.OAuth.ClientId));
        BackchannelPaths paths = Install(gateway);
        BackchannelPaths.Held refresh = paths.Hold("/token");
        BackchannelPaths.Held revocation = paths.Hold("/revoke");
        store.BeforeReplace = RemoveOnFirst(store);

        Task<IdentityGateway.Answer> holder = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar), Ct);
        await refresh.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        TaskCompletionSource read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterGet = _ =>
        {
            read.TrySetResult();
            return Task.CompletedTask;
        };
        Task<IdentityGateway.Answer> waiter = Task.Run(() => FragmentRefreshTests.TokenAsync(gateway, jar), Ct);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        store.AfterGet = null;
        refresh.Release();

        try
        {
            await revocation.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            IdentityGateway.Answer queued = await waiter.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal((401, "unauthenticated"), (queued.Status, queued.Body));
            Assert.False(holder.IsCompleted, "the refresh answered before its revocation ended");
        }
        finally
        {
            revocation.Release();
        }

        IdentityGateway.Answer answer = await holder;
        Assert.Equal((401, "unauthenticated"), (answer.Status, answer.Body));
        Assert.Equal("revoked", Field(Assert.Single(OrphanLines(logs)), "Outcome"));
    }

    [Fact]
    public async Task A_write_that_ends_superseded_revokes_nothing()
    {
        // #1544's precaution: another writer replaced the set while the provider answered, so the record
        // still holds the set's grant, and the successor is dropped unrevoked. Mutation: revoke on every
        // write that does not land; a revocation is recorded, and this fails.
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, _) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(Shell.OAuth.ClientId));
        int seen = store.Replaces;
        store.BeforeReplace = (k, attempt) => attempt == seen + 1 ? RotateElsewhereAsync(store, k) : Task.CompletedTask;

        IdentityGateway.Answer answer = await FragmentRefreshTests.TokenAsync(gateway, jar);

        Assert.Equal(503, answer.Status);
        Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Empty(gateway.Idp.Revocations);
        Assert.Contains(logs.Lines, l => l.Message.Contains("ended superseded after 1 versioned replaces", StringComparison.Ordinal));
        Assert.Empty(OrphanLines(logs));
    }

    [Fact]
    public async Task A_write_that_ends_exhausted_revokes_nothing()
    {
        // #1544's precaution: every replace loses to a writer that keeps the presented refresh token, so the
        // record still holds the set's grant; the call answers its own token and revokes nothing. Mutation:
        // set the successor for revocation on the exhausted end too; a revocation is recorded, and this
        // fails (1 test red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, _) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(Shell.OAuth.ClientId));
        int seen = store.Replaces;
        store.BeforeReplace = (k, attempt) => attempt <= seen + AccessTokenSource.WriteAttempts ? store.BumpAsync(k) : Task.CompletedTask;

        IdentityGateway.Answer answer = await FragmentRefreshTests.TokenAsync(gateway, jar);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (answer.Status, answer.Body));
        Assert.Empty(gateway.Idp.Revocations);
        Assert.Contains(logs.Lines, l => l.Message.Contains("ended exhausted after 3 versioned replaces", StringComparison.Ordinal));
        Assert.Empty(OrphanLines(logs));
    }

    [Theory]
    [InlineData("refused", "http_400")]
    [InlineData("deadline", "deadline_exceeded")]
    [InlineData("unreachable", "unreachable")]
    public async Task A_revocation_that_fails_logs_its_outcome_and_the_call_still_answers_unauthenticated(string fault, string outcome)
    {
        // #1544: whatever the revocation's outcome, the call answers unauthenticated and the line says how it
        // ended, with no token in any line (N-16). The deadline is the revocation's own ProviderDeadline,
        // shortened only once the refresh has its answer. Mutation: write the line only for a revocation
        // that succeeded; no line is found, and this fails (3 tests red).
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(Shell.OAuth.ClientId));
        BackchannelPaths paths = Install(gateway);
        AccessTokenSourceOptions options = gateway.Services.GetRequiredService<AccessTokenSourceOptions>();
        Func<string, int, Task> remove = RemoveOnFirst(store);
        store.BeforeReplace = (k, attempt) =>
        {
            if (fault == "deadline")
            {
                options.ProviderDeadline = TimeSpan.FromMilliseconds(200);
            }

            return remove(k, attempt);
        };
        BackchannelPaths.Held? held = null;
        switch (fault)
        {
            case "refused":
                gateway.Idp.Faults.RevokeFailureStatus = 400;
                break;
            case "deadline":
                held = paths.Hold("/revoke");
                break;
            default:
                paths.Fail("/revoke");
                break;
        }

        IdentityGateway.Answer answer;
        try
        {
            answer = await FragmentRefreshTests.TokenAsync(gateway, jar);
        }
        finally
        {
            held?.Release();
        }

        Assert.Equal((401, "unauthenticated"), (answer.Status, answer.Body));
        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.True(gateway.Idp.IsRedeemable(grant.RefreshTokenIssued!));
        CapturedLine line = Assert.Single(OrphanLines(logs));
        Assert.Equal(outcome, Field(line, "Outcome"));
        string all = string.Join("\n", logs.Lines.Select(l => l.Text));
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
        TokenSearch.AssertNoTokens(gateway.Idp, all);
    }

    [Fact]
    public async Task At_a_provider_that_does_not_rotate_the_presented_refresh_token_is_the_one_revoked()
    {
        // Adam's decision on #1544: a provider that does not rotate answers no refresh token (T-A13), so
        // the grant the refresh presented is the one no record holds, and it is revoked. The library's
        // token then carries the refresh token presented, which is why the gateway needs no fallback of
        // its own; this test fails if a library update stops carrying it, and the path revokes nothing.
        CapturedLog logs = new();
        ScriptedSessionStore store = new(new InMemorySessionStore());
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [Shell], s => s.AddSingleton(logs.Provider), sessions: new RecordingSessionStore(store));
        (Dictionary<string, string> jar, string key) = await FragmentRefreshTests.SignInAsync(gateway, lifetimeOf: DueFor(Shell.OAuth.ClientId));
        string presented = (await FragmentRefreshTests.PropertiesAsync(store, key)).GetTokenValue("refresh_token")!;
        gateway.Idp.Faults.RotateRefreshTokens = false;
        store.BeforeReplace = RemoveOnFirst(store);

        IdentityGateway.Answer answer = await FragmentRefreshTests.TokenAsync(gateway, jar);

        Assert.Equal((401, "unauthenticated"), (answer.Status, answer.Body));
        Assert.Equal(presented, Assert.Single(gateway.Idp.Revocations).Token);
        Assert.False(gateway.Idp.IsRedeemable(presented));
        Assert.Equal("revoked", Field(Assert.Single(OrphanLines(logs)), "Outcome"));
    }

    /// <summary>Only <paramref name="client"/>'s set due at sign-in.</summary>
    private static Func<string, TimeSpan> DueFor(string client) =>
        c => c == client ? FragmentRefreshTests.Due : FragmentRefreshTests.Fresh;

    /// <summary>Removes the session just before the refresh's first versioned replace: the logout on another task.</summary>
    private static Func<string, int, Task> RemoveOnFirst(ScriptedSessionStore store)
    {
        int seen = store.Replaces;
        return (k, attempt) => attempt == seen + 1 ? store.Inner.RemoveAsync(k, Ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites the shell's set one version on with another refresh token, as another task's refresh would,
    /// and no access token.
    /// </summary>
    private static async Task RotateElsewhereAsync(ScriptedSessionStore store, string key)
    {
        SessionRecord record = Assert.IsType<SessionRecord>(await store.Inner.GetAsync(key, Ct));
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        ticket.Properties.StoreTokens([.. ticket.Properties.GetTokens()
            .Where(t => t.Name != "access_token")
            .Select(t => t.Name == "refresh_token" ? new AuthenticationToken { Name = t.Name, Value = "rotated-elsewhere" } : t)]);
        Assert.True(await store.Inner.ReplaceAsync(
            key, record with { Version = record.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) }, record.Version, Ct));
    }

    /// <summary>The shell's backchannel, the refresh's and the revocation's, replaced by one whose paths a test holds or fails.</summary>
    private static BackchannelPaths Install(IdentityGateway gateway)
    {
        BackchannelPaths paths = new();
        gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc(Shell.AppId)).Backchannel = new HttpClient(paths);
        return paths;
    }

    /// <summary>
    /// A backchannel that passes every request to the provider, except one to a path a test holds, which
    /// waits for its release (or its caller's cancellation), and one to a path a test fails, which throws
    /// as an unreachable provider does.
    /// </summary>
    private sealed class BackchannelPaths() : DelegatingHandler(new HttpClientHandler())
    {
        private readonly ConcurrentDictionary<string, Held> held = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> failing = new(StringComparer.Ordinal);

        public Held Hold(string path) => held[path] = new Held();

        public void Fail(string path) => failing[path] = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (failing.ContainsKey(path))
            {
                throw new HttpRequestException("The test made the provider unreachable.");
            }

            if (held.TryGetValue(path, out Held? hold))
            {
                hold.Enter();
                await hold.Released.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        internal sealed class Held
        {
            private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => entered.Task;

            public Task Released => released.Task;

            public void Enter() => entered.TrySetResult();

            public void Release() => released.TrySetResult();
        }
    }

    /// <summary>The orphaned successor lines: the token source's lines that name the successor.</summary>
    private static CapturedLine[] OrphanLines(CapturedLog logs) =>
        [.. logs.Lines.Where(l => l.Category == typeof(AccessTokenSource).FullName && l.Message.Contains("successor", StringComparison.Ordinal))];

    private static string[] Fields(CapturedLine line) =>
        [.. line.State.Select(p => p.Key).Where(k => k != "{OriginalFormat}").Order(StringComparer.Ordinal)];

    private static object? Field(CapturedLine line, string name) => Assert.Single(line.State, p => p.Key == name).Value;
}
