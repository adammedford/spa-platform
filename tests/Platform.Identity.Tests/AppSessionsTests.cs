using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <see cref="AppSessions"/> at its own interface, over <c>Platform.Testing</c>'s
/// <see cref="InMemorySessionStore"/>: no identity stack and no request pipeline. Each test names, in
/// its body, the change to the module that turns it red.
/// </summary>
public sealed class AppSessionsTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1");

    // One array for every record, because a record's equality compares its ticket's memory by reference.
    private static readonly byte[] Ticket = [1, 2, 3];

    [Fact]
    public async Task EnterAsync_lets_one_caller_at_a_time_hold_a_session_key()
    {
        // ADR-0017 decision 1: one refresh at a time per session. Mutation: build the gate's semaphore
        // as SemaphoreSlim(2, 2); the second caller enters while the first holds the key, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);
        string other = await store.CreateAsync(Record(App1), Ct);

        AppSessions.Turn first = await sessions.EnterAsync(App1, key);
        Task<AppSessions.Turn> second = sessions.EnterAsync(App1, key);
        using AppSessions.Turn elsewhere = await sessions.EnterAsync(App1, other);

        Assert.False(second.IsCompleted);
        first.Dispose();
        using AppSessions.Turn next = await second;
        Assert.NotNull(next.Owned);
    }

    [Fact]
    public async Task ReplaceAsync_writes_only_over_the_version_the_lease_read()
    {
        // T-D2: another gateway task sharing the store writes the session between this turn's read and
        // its write, arranged here as a direct store write between EnterAsync and ReplaceAsync: the race
        // the token source's tests once placed by counting store reads (#167). The stale write must lose
        // and leave the winner. Mutation: take the expected version from a fresh read of the store
        // instead of from Owned, which makes the write unversioned; the stale replace succeeds over the
        // winner, and this fails. A loss changes nothing on the turn either. Mutation: set Owned to the
        // record offered before the store answers; the turn claims a write that lost, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1, version: 1), Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);
        SessionRecord owned = Assert.IsType<SessionRecord>(turn.Owned);

        SessionRecord winner = Record(App1, version: 2, subject: "winner");
        Assert.True(await store.ReplaceAsync(key, winner, expectedVersion: 1, Ct));

        Assert.False(await turn.ReplaceAsync(owned with { Version = 2, Subject = "stale" }));
        Assert.Equal(winner, await store.GetAsync(key, Ct));
        Assert.Equal(owned, turn.Owned);
    }

    [Fact]
    public async Task ReplaceAsync_wins_when_nobody_wrote_in_between()
    {
        // The other half of the versioned replace, so the test above cannot pass by refusing everything.
        // Mutation: return false unconditionally; this fails. The second write is versioned at the first
        // write's version, so a constant expected version (the ticket's mutation; any constant, the
        // version the turn read included) cannot win both writes, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1, version: 1), Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);
        SessionRecord next = Assert.IsType<SessionRecord>(turn.Owned) with { Version = 2, Subject = "rotated" };

        Assert.True(await turn.ReplaceAsync(next));
        Assert.Equal(next, await store.GetAsync(key, Ct));
        Assert.Equal(next, turn.Owned);

        SessionRecord after = next with { Version = 3, Subject = "rotated again" };
        Assert.True(await turn.ReplaceAsync(after));
        Assert.Equal(after, await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task EndAsync_removes_the_session_when_its_action_throws()
    {
        // "Revoke, then remove whatever the provider said" (#141, Q6). Mutation: move the remove out of
        // the finally, after the action; the throwing action leaves the session, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);
        SessionRecord? given = null;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => turn.EndAsync(record =>
        {
            given = record;
            throw new InvalidOperationException("the revocation failed");
        }));

        Assert.Equal("the revocation failed", thrown.Message);
        Assert.Equal(Record(App1), given);
        Assert.Null(await store.GetAsync(key, Ct));
        Assert.Null(turn.Owned);
    }

    [Fact]
    public async Task EndAsync_runs_its_action_while_the_store_still_holds_the_session()
    {
        // "Revoke, then remove" (#141, Q6): the action runs first and the removal after it, so logout's
        // revocation reads a session that is still there. The throwing-action test above cannot see the
        // order, since the removal runs either way. Mutation: swap the order in EndAsync (remove, then
        // run the action); the action finds the store empty, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);
        SessionRecord? storedWhenTheActionRan = null;

        await turn.EndAsync(async _ => storedWhenTheActionRan = await store.GetAsync(key, Ct));

        Assert.Equal(Record(App1), storedWhenTheActionRan);
        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task WhileWaiting_hands_a_queued_caller_the_outcome_of_the_refresh_it_waited_behind()
    {
        // T-D6: a caller queued behind a failed refresh shares the failure rather than asking the provider
        // again. Mutation: make WhileWaiting always null; the queued caller sees no outcome and would
        // re-ask the provider, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);

        AppSessions.Turn holder = await sessions.EnterAsync(App1, key);
        Assert.Null(holder.WhileWaiting);
        Task<AppSessions.Turn> queued = sessions.EnterAsync(App1, key);
        holder.Finish(new RefreshOutcome.Unavailable());
        Task<AppSessions.Turn> arrivedAfter = sessions.EnterAsync(App1, key);
        holder.Dispose();

        AppSessions.Turn waiter = await queued;
        Assert.IsType<RefreshOutcome.Unavailable>(waiter.WhileWaiting);
        waiter.Dispose();

        // A caller that queued after the refresh ended waited behind none, so it has no outcome to share.
        using AppSessions.Turn later = await arrivedAfter;
        Assert.Null(later.WhileWaiting);
    }

    [Fact]
    public async Task ReadAsync_returns_null_for_another_clients_record()
    {
        // N-02, N-22, task 3.6: a session is bound to its app and its client, so an app rotated onto a new
        // client reads its old sessions as absent. Mutation: drop the client comparison from the ownership
        // check; the cross-client read returns the record, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string own = await store.CreateAsync(Record(App1), Ct);
        string otherClient = await store.CreateAsync(Record(App1, clientId: "client-before-rotation"), Ct);
        string otherApp = await store.CreateAsync(Record(TestApps.Entry("app2")), Ct);

        Assert.Equal(Record(App1), await sessions.ReadAsync(App1, own, Ct));
        Assert.Null(await sessions.ReadAsync(App1, otherClient, Ct));
        Assert.Null(await sessions.ReadAsync(App1, otherApp, Ct));
        Assert.Null(await sessions.ReadAsync(App1, "no-such-key", Ct));
    }

    [Fact]
    public async Task A_lease_over_another_clients_record_owns_nothing_and_neither_replaces_nor_removes_it()
    {
        // The turn's half of the ownership rule. Mutation: drop the Owned check at the top of EndAsync
        // (end by key whatever the turn owns); another client's session is removed, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        SessionRecord foreign = Record(App1, clientId: "client-before-rotation");
        string key = await store.CreateAsync(foreign, Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);
        bool ran = false;

        Assert.Null(turn.Owned);
        Assert.False(await turn.ReplaceAsync(foreign with { Version = 2, Subject = "mallory" }));
        await turn.EndAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.False(ran);
        Assert.Equal(foreign, await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task ActiveGates_returns_to_zero_once_every_lease_is_disposed()
    {
        // The table is bounded by the calls in flight, not by the sessions that ever entered. Mutation:
        // skip the last leaver's remove from the table; the count stays at two, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string first = await store.CreateAsync(Record(App1), Ct);
        string second = await store.CreateAsync(Record(App1), Ct);

        AppSessions.Turn a = await sessions.EnterAsync(App1, first);
        Task<AppSessions.Turn> queued = sessions.EnterAsync(App1, first);
        AppSessions.Turn b = await sessions.EnterAsync(App1, second);
        Assert.Equal(2, sessions.ActiveGates);

        a.Dispose();
        (await queued).Dispose();
        b.Dispose();

        Assert.Equal(0, sessions.ActiveGates);
    }

    [Fact]
    public async Task OpenAsync_issues_a_new_key_on_a_request_that_presented_one_and_records_its_reference()
    {
        // N-18 (T12, ADR-0004): a login over a planted session never carries the new identity under the
        // planted key, and the request line names the session it was given. Mutation: have OpenAsync
        // return the key already on the request's channel when there is one instead of creating, or skip
        // its session_ref write; the distinct-key or the reference assertion fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string planted = await store.CreateAsync(Record(App1, subject: "mallory"), Ct);
        (HttpContext request, SettableRequestTelemetryFeature line) = Request();
        Assert.NotNull(await sessions.ResolveAsync(request, App1, planted, Ct));

        string key = await sessions.OpenAsync(request, Record(App1), Ct);
        string another = await sessions.OpenAsync(Request().Context, Record(App1), Ct);

        Assert.NotEqual(planted, key);
        Assert.NotEqual(key, another);
        Assert.Equal(Record(App1), await store.GetAsync(key, Ct));
        Assert.Equal("mallory", (await store.GetAsync(planted, Ct))!.Subject);
        Assert.Equal(SessionRef.Of(key), line.SessionRef);
        Assert.Equal(key, sessions.KeyOf(request, Succeeded()));
    }

    [Fact]
    public async Task ResolveAsync_references_a_foreign_or_absent_key_and_resolves_no_session_from_it()
    {
        // N-02, N-22, task 3.8: a cookie naming another app's or another client's session, or one that is
        // gone, resolves to no key a caller could act on; its reference still reaches the request line, so
        // the auth.session rejection correlates with the session it named (SessionIsolationTests).
        // Mutation: put the key on the channel before the ownership read rather than after it; KeyOf hands
        // back the foreign key, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string otherApp = await store.CreateAsync(Record(TestApps.Entry("app2")), Ct);
        string otherClient = await store.CreateAsync(Record(App1, clientId: "client-before-rotation"), Ct);

        foreach (string key in new[] { otherApp, otherClient, "no-such-key" })
        {
            (HttpContext request, SettableRequestTelemetryFeature line) = Request();

            Assert.Null(await sessions.ResolveAsync(request, App1, key, Ct));

            Assert.Equal(SessionRef.Of(key), line.SessionRef);
            Assert.Null(sessions.KeyOf(request, Succeeded()));
        }
    }

    [Fact]
    public async Task RemoveAsync_removes_an_owned_record_leaves_a_foreign_one_and_can_be_called_again()
    {
        // The ungated owned-remove the cookie handler reaches through the ticket store (#141, Q12): its
        // sign-out after a gated end finds nothing and does nothing, and a foreign key is never removed
        // (N-02, N-22). Mutation: drop the ownership check (remove by key whatever the record is);
        // another app's and another client's sessions are removed, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string own = await store.CreateAsync(Record(App1), Ct);
        string otherApp = await store.CreateAsync(Record(TestApps.Entry("app2")), Ct);
        string otherClient = await store.CreateAsync(Record(App1, clientId: "client-before-rotation"), Ct);

        await sessions.RemoveAsync(App1, own, Ct);
        await sessions.RemoveAsync(App1, own, Ct);
        await sessions.RemoveAsync(App1, otherApp, Ct);
        await sessions.RemoveAsync(App1, otherClient, Ct);

        Assert.Null(await store.GetAsync(own, Ct));
        Assert.NotNull(await store.GetAsync(otherApp, Ct));
        Assert.NotNull(await store.GetAsync(otherClient, Ct));
    }

    [Fact]
    public async Task RemoveAsync_does_not_wait_for_a_lease_held_on_the_same_key()
    {
        // Ungated by necessity: the token source ends a session and then signs out while it still holds
        // the turn, and the sign-out's remove reaches this member; the gate is not re-entrant, so a
        // gated remove there would wait on itself (#141, Q12). Mutation: enter the gate in RemoveAsync;
        // the remove waits on the held turn, the deadline passes, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);
        using AppSessions.Turn turn = await sessions.EnterAsync(App1, key);

        await sessions.RemoveAsync(App1, key, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task The_session_key_leaves_the_module_only_as_its_reference()
    {
        // N-16, contracts/telemetry.md: the session key is on the never-logged list, and what the module
        // leaves for the request line is SessionRef.Of(key), on the sign-in, on a request presenting a
        // live session and on one presenting a dead one. The module never logs, so the request line is
        // the only line it reaches. Mutation: write the key in place of SessionRef.Of(key) in the
        // module's one session_ref write; the search finds the key, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        (HttpContext signIn, SettableRequestTelemetryFeature signInLine) = Request();
        string key = await sessions.OpenAsync(signIn, Record(App1), Ct);
        (HttpContext live, SettableRequestTelemetryFeature liveLine) = Request();
        await sessions.ResolveAsync(live, App1, key, Ct);
        await store.RemoveAsync(key, Ct);
        (HttpContext dead, SettableRequestTelemetryFeature deadLine) = Request();
        await sessions.ResolveAsync(dead, App1, key, Ct);
        sessions.KeyOf(dead, AuthenticateResult.Fail("Unprotect ticket failed"));

        foreach (SettableRequestTelemetryFeature line in new[] { signInLine, liveLine, deadLine })
        {
            Assert.Equal(SessionRef.Of(key), line.SessionRef);
            Assert.All([line.SessionRef, line.Rule], value => Assert.DoesNotContain(key, value ?? "", StringComparison.Ordinal));
        }
    }

    private static (HttpContext Context, SettableRequestTelemetryFeature Line) Request()
    {
        DefaultHttpContext context = new();
        SettableRequestTelemetryFeature line = new();
        context.Features.Set<IRequestTelemetryFeature>(line);
        return (context, line);
    }

    /// <summary>What the cookie handler answers for a cookie it read and accepted.</summary>
    private static AuthenticateResult Succeeded() =>
        AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), "test"));

    private static SessionRecord Record(AppDefinition app, long version = 1, string subject = "alice", string? clientId = null) =>
        new(app.AppId, clientId ?? app.OAuth.ClientId, subject,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(8), version, Ticket);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
