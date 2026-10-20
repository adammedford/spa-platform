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

        AppSessions.Lease first = await sessions.EnterAsync(App1, key);
        Task<AppSessions.Lease> second = sessions.EnterAsync(App1, key);
        using AppSessions.Lease elsewhere = await sessions.EnterAsync(App1, other);

        Assert.False(second.IsCompleted);
        first.Dispose();
        using AppSessions.Lease next = await second;
        Assert.NotNull(next.Owned);
    }

    [Fact]
    public async Task ReplaceAsync_writes_only_over_the_version_the_lease_read()
    {
        // T-D2: another gateway task sharing the store writes the session between this lease's read and
        // its write. The stale write must lose and leave the winner. Mutation: take the expected version
        // from a fresh read of the store instead of from Owned, which makes the write unversioned; the
        // stale replace succeeds over the winner, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1, version: 1), Ct);
        using AppSessions.Lease lease = await sessions.EnterAsync(App1, key);
        SessionRecord owned = Assert.IsType<SessionRecord>(lease.Owned);

        SessionRecord winner = Record(App1, version: 2, subject: "winner");
        Assert.True(await store.ReplaceAsync(key, winner, expectedVersion: 1, Ct));

        Assert.False(await lease.ReplaceAsync(owned with { Version = 2 }));
        Assert.Equal(winner, await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task ReplaceAsync_wins_when_nobody_wrote_in_between()
    {
        // The other half of the versioned replace, so the test above cannot pass by refusing everything.
        // Mutation: return false unconditionally; this fails. The second write is versioned at the first
        // write's version, so a constant expected version (the ticket's mutation; any constant, the
        // version the lease read included) cannot win both writes, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1, version: 1), Ct);
        using AppSessions.Lease lease = await sessions.EnterAsync(App1, key);
        SessionRecord next = Assert.IsType<SessionRecord>(lease.Owned) with { Version = 2, Subject = "rotated" };

        Assert.True(await lease.ReplaceAsync(next));
        Assert.Equal(next, await store.GetAsync(key, Ct));
        Assert.Equal(next, lease.Owned);

        SessionRecord after = next with { Version = 3, Subject = "rotated again" };
        Assert.True(await lease.ReplaceAsync(after));
        Assert.Equal(after, await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task EndAsync_removes_the_session_when_its_action_throws()
    {
        // ADR-0017 decision 4: revoke, then remove whatever the provider said. Mutation: move the remove
        // out of the finally, after the action; the throwing action leaves the session, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        string key = await store.CreateAsync(Record(App1), Ct);
        using AppSessions.Lease lease = await sessions.EnterAsync(App1, key);
        SessionRecord? given = null;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EndAsync(record =>
        {
            given = record;
            throw new InvalidOperationException("the revocation failed");
        }));

        Assert.Equal("the revocation failed", thrown.Message);
        Assert.Equal(Record(App1), given);
        Assert.Null(await store.GetAsync(key, Ct));
        Assert.Null(lease.Owned);
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

        AppSessions.Lease holder = await sessions.EnterAsync(App1, key);
        Assert.Null(holder.WhileWaiting);
        Task<AppSessions.Lease> queued = sessions.EnterAsync(App1, key);
        holder.Finish(new RefreshOutcome.Unavailable());
        Task<AppSessions.Lease> arrivedAfter = sessions.EnterAsync(App1, key);
        holder.Dispose();

        AppSessions.Lease waiter = await queued;
        Assert.IsType<RefreshOutcome.Unavailable>(waiter.WhileWaiting);
        waiter.Dispose();

        // A caller that queued after the refresh ended waited behind none, so it has no outcome to share.
        using AppSessions.Lease later = await arrivedAfter;
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
        // The lease's half of the ownership rule. Mutation: drop the Owned check at the top of EndAsync
        // (end by key whatever the lease owns); another client's session is removed, and this fails.
        InMemorySessionStore store = new();
        AppSessions sessions = new(store);
        SessionRecord foreign = Record(App1, clientId: "client-before-rotation");
        string key = await store.CreateAsync(foreign, Ct);
        using AppSessions.Lease lease = await sessions.EnterAsync(App1, key);
        bool ran = false;

        Assert.Null(lease.Owned);
        Assert.False(await lease.ReplaceAsync(foreign with { Version = 2, Subject = "mallory" }));
        await lease.EndAsync(_ =>
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

        AppSessions.Lease a = await sessions.EnterAsync(App1, first);
        Task<AppSessions.Lease> queued = sessions.EnterAsync(App1, first);
        AppSessions.Lease b = await sessions.EnterAsync(App1, second);
        Assert.Equal(2, sessions.ActiveGates);

        a.Dispose();
        (await queued).Dispose();
        b.Dispose();

        Assert.Equal(0, sessions.ActiveGates);
    }

    private static SessionRecord Record(AppDefinition app, long version = 1, string subject = "alice", string? clientId = null) =>
        new(app.AppId, clientId ?? app.OAuth.ClientId, subject,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(8), version, Ticket);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
