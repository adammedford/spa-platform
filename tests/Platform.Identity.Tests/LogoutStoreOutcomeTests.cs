using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;
using Platform.Testing;
using Fault = Platform.Identity.Tests.FakeDynamoDbServer.Fault;

namespace Platform.Identity.Tests;

/// <summary>
/// F11's caller half (#386, under #332): what <c>POST /bff/logout</c> does with the session store's
/// answers. A remove still unknown at <c>StoreDeadline</c> is reported not confirmed on one line and the
/// logout goes on as usual (Q187); a store read that fails earlier in the logout keeps F17's 500 with
/// both cookies intact (Q188); and once the end has run, the cookie handler's sign-out makes no store
/// call of its own (Q192).
/// </summary>
public sealed class LogoutStoreOutcomeTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task P2_1_F11_a_logout_whose_remove_is_unknown_at_StoreDeadline_redirects_as_usual_clears_both_cookies_and_logs_one_line()
    {
        // Q187. The DeleteItem is held, unapplied, until the table's clock passes StoreDeadline; the
        // store's retry runs inside the same deadline, so the remove ends "not confirmed". The table's
        // clock starts at the real time, because the cookie handler stamps the record's expiry on the
        // system clock and the table reads it as absent once its own clock passes that.
        // Mutation: let SessionStoreUnavailableException escape SessionEnd.EndAsync, as before #386, and
        // the logout throws rather than answering.
        await using FakeDynamoDbServer fake = await FakeDynamoDbServer.StartAsync(Ct);
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        CapturedLog log = new();
        // The recording wrapper hands the test the session key itself, so the key is a canary below.
        RecordingSessionStore sessions = new(new DynamoDbSessionStore(
            DynamoDbTables.Over(fake, clock, log.CreateLogger<DynamoDbSessionStore>())));
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1],
            services => services.AddSingleton(log.Provider),
            stubProfile: false,
            sessions: sessions);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        string key = Assert.Single(sessions.Created);
        string pk = Assert.Single(fake.Items.Keys);
        fake.Next("DeleteItem", Fault.HoldBeforeCommit);

        // The lines the logout wrote: the host's start-up writes its own warning about the key ring.
        int beforeLogout = log.Lines.Count;
        Task<IdentityGateway.Answer> sent = Task.Run(() => gateway.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST"), Ct);
        await fake.Held.Task.WaitAsync(Ct);
        clock.Advance(DynamoDbSessionTable.DefaultStoreDeadline);
        IdentityGateway.Answer logout = await sent.WaitAsync(Ct);

        // What the browser sees is the usual logout.
        Assert.Equal(302, logout.Status);
        Assert.Equal(gateway.Idp.EndSessionEndpoint, new Uri(logout.Location).GetLeftPart(UriPartial.Path));
        Assert.True(Expired(logout.Cookies, login.Callback.SessionCookie.Name.Value!));
        Assert.True(Expired(logout.Cookies, FakeCsrfTokens.CookieName));

        // One line for the operator, naming the session by its reference and the operation, and nothing
        // a log must not carry (N-16).
        CapturedLine line = Assert.Single(log.Lines.Skip(beforeLogout), l => l.Level >= LogLevel.Warning);
        Assert.Equal(typeof(SessionEnd).FullName, line.Category);
        Assert.Contains(logout.SessionRef!, line.Text, StringComparison.Ordinal);
        Assert.Contains("remove (DeleteItem)", line.Text, StringComparison.Ordinal);
        Assert.Contains("not confirmed", line.Text, StringComparison.Ordinal);
        string all = string.Join("\n", log.Lines.Select(l => l.Text));
        Assert.DoesNotContain(key, all, StringComparison.Ordinal);
        Assert.DoesNotContain(pk, all, StringComparison.Ordinal);
        Assert.DoesNotContain(login.Callback.SessionCookie.Value.Value!, all, StringComparison.Ordinal);
        TokenSearch.AssertNoTokens(gateway.Idp, all);

        // No second remove followed the first (Q192): the held DeleteItem never applied, and the record
        // is still in the table, which only a later DeleteItem could have changed.
        Assert.Equal(1, fake.Operations.Count(o => o == "DeleteItem"));
        Assert.True(fake.Items.ContainsKey(pk));
    }

    [Theory]
    [InlineData(1, "the cookie handler's read")]
    [InlineData(2, "the read under the session's gate")]
    public async Task P2_1_F11_a_logout_whose_earlier_store_read_fails_keeps_F17s_500_with_both_cookies_intact(int failingRead, string which)
    {
        // Q188. Only the end's remove is caught: a read that fails before it, whether the cookie handler's
        // or SessionEnd's under the gate, still ends on the server's generic 500 with no cookie set or
        // cleared. Mutation: widen SessionEnd's catch to the whole of EndAsync, gate entry included, and the
        // second case answers 302 with both cookies cleared.
        _ = which;
        ScriptedReads reads = new(new InMemorySessionStore());
        RecordingSessionStore sessions = new(reads);
        CapturedLog log = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton(log.Provider), sessions: sessions);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await sessions.SingleAsync();

        await using StageHost server = await StageHost.StartAsync(
            new StageHostOptions { Pipeline = app => app.Run(context => gateway.ServeAsync(context, App1)) },
            Ct);
        reads.FailRead = failingRead;

        using HttpRequestMessage request = new(HttpMethod.Post, IdentityGateway.LogoutPath)
        {
            Content = new FormUrlEncodedContent([]),
        };
        request.Headers.Add("Cookie", string.Join("; ", login.Jar.Select(c => $"{c.Key}={c.Value}")));
        using HttpResponseMessage answer = await server.Client.SendAsync(request, Ct);

        Assert.Equal(500, (int)answer.StatusCode);
        Assert.False(answer.Headers.Contains("Set-Cookie"));
        CapturedLine error = Assert.Single([.. server.Log.Lines, .. log.Lines], l => l.Level >= LogLevel.Error);
        Assert.Contains("read (GetItem)", error.Text, StringComparison.Ordinal);
        Assert.Empty(sessions.Removed);
        reads.FailRead = null;
        Assert.NotNull(await sessions.GetAsync(key, Ct));
    }

    [Fact]
    public async Task P2_1_F11_once_the_end_has_run_the_cookie_handlers_sign_out_makes_no_store_call_of_its_own()
    {
        // Q192. A logout's store calls are the cookie handler's read of the session, the read under the
        // session's gate and the end's remove, and nothing after it. Mutation: drop the check in
        // AppSessions.RemoveAsync for the request's ended session, and the sign-out's read through the
        // ticket store adds a GetItem after the DeleteItem.
        await using FakeDynamoDbServer fake = await FakeDynamoDbServer.StartAsync(Ct);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1],
            services => services.AddSingleton<ISessionStore>(new DynamoDbSessionStore(DynamoDbTables.Over(fake, TimeProvider.System))),
            stubProfile: false);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        int before = fake.Operations.Count;

        IdentityGateway.Answer logout = await gateway.SendAsync(App1, IdentityGateway.LogoutPath, login.Jar, "POST");

        Assert.Equal(302, logout.Status);
        Assert.Equal(["GetItem", "GetItem", "DeleteItem"], fake.Operations.Skip(before));
        Assert.Empty(fake.Items);
    }

    private static bool Expired(IReadOnlyList<SetCookieHeaderValue> cookies, string name) =>
        cookies.Any(c => c.Name == name && c.Expires is { } expires && expires < DateTimeOffset.UtcNow);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A session store whose reads fail on cue: once <see cref="FailRead"/> is set, the read with that
    /// number, counting from one after it was set, throws the store's "did not complete" as the DynamoDB
    /// store does when a GetItem fails (F17). Every other call passes through.
    /// </summary>
    private sealed class ScriptedReads(ISessionStore inner) : ISessionStore
    {
        private int? failRead;
        private int reads;

        public int? FailRead
        {
            get => failRead;
            set
            {
                failRead = value;
                reads = 0;
            }
        }

        public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

        public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) =>
            failRead is { } n && Interlocked.Increment(ref reads) == n
                ? throw new SessionStoreUnavailableException("read (GetItem)", DynamoDbSessionTable.DefaultStoreDeadline, null)
                : inner.GetAsync(key, ct);

        public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
            inner.ReplaceAsync(key, record, expectedVersion, ct);

        public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

        public Task RemoveAppAsync(string appId, CancellationToken ct) => inner.RemoveAppAsync(appId, ct);
    }
}
