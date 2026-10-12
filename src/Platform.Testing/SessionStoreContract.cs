using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// The behaviour every <see cref="ISessionStore"/> must have. Subclass it once per implementation and
/// override <see cref="CreateStore"/>. Task 3.4: "a store contract suite passes. The Phase 2 store
/// must pass the same suite."
///
/// What is deliberately not here: the capacity bound and the expiry sweep. Task 3.4 gives those to
/// the in-memory store in <c>Platform.Identity</c>, and a double that evicted under load would make
/// another workstream's test flake. A store is free to add them; nothing here forbids it.
///
/// <b>Draft until G1b</b>, with <see cref="SessionRecord"/>: task I-6 folds the G0 findings in.
/// </summary>
public abstract class SessionStoreContract
{
    protected abstract ISessionStore CreateStore();

    [Fact]
    public async Task A_created_session_reads_back_under_the_key_it_returned()
    {
        ISessionStore store = CreateStore();
        SessionRecord record = Record();

        string key = await store.CreateAsync(record, Ct);

        Assert.Equal(record, await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task Every_created_session_gets_its_own_key()
    {
        // Two logins to the same app by the same subject are two sessions.
        ISessionStore store = CreateStore();

        string first = await store.CreateAsync(Record(), Ct);
        string second = await store.CreateAsync(Record(), Ct);

        Assert.NotEqual(first, second);
        Assert.NotNull(await store.GetAsync(first, Ct));
        Assert.NotNull(await store.GetAsync(second, Ct));
    }

    [Fact]
    public async Task A_key_that_was_never_created_reads_as_null()
    {
        ISessionStore store = CreateStore();

        Assert.Null(await store.GetAsync("no-such-key", Ct));
    }

    [Fact]
    public async Task A_replace_at_the_expected_version_stores_the_new_record()
    {
        ISessionStore store = CreateStore();
        string key = await store.CreateAsync(Record(version: 1), Ct);

        Assert.True(await store.ReplaceAsync(key, Record(version: 2, subject: "renewed"), expectedVersion: 1, Ct));

        SessionRecord? stored = await store.GetAsync(key, Ct);
        Assert.NotNull(stored);
        Assert.Equal("renewed", stored.Subject);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task A_replace_at_a_stale_version_is_refused_and_changes_nothing()
    {
        // Two requests refreshing one session at once. Task 3.8 re-reads the session before letting
        // a refusal end it, and this is the rule that makes the re-read meaningful.
        ISessionStore store = CreateStore();
        string key = await store.CreateAsync(Record(version: 1), Ct);
        await store.ReplaceAsync(key, Record(version: 2, subject: "first-writer"), expectedVersion: 1, Ct);

        Assert.False(await store.ReplaceAsync(key, Record(version: 2, subject: "second-writer"), expectedVersion: 1, Ct));

        SessionRecord? stored = await store.GetAsync(key, Ct);
        Assert.NotNull(stored);
        Assert.Equal("first-writer", stored.Subject);
    }

    [Fact]
    public async Task A_replace_of_a_key_that_is_not_there_is_refused()
    {
        ISessionStore store = CreateStore();

        Assert.False(await store.ReplaceAsync("no-such-key", Record(), expectedVersion: 1, Ct));
    }

    [Fact]
    public async Task A_removed_session_is_gone()
    {
        ISessionStore store = CreateStore();
        string key = await store.CreateAsync(Record(), Ct);

        await store.RemoveAsync(key, Ct);

        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task Removing_a_key_that_is_not_there_is_not_an_error()
    {
        // Sign-out runs on a session the sweep may already have taken.
        ISessionStore store = CreateStore();

        await store.RemoveAsync("no-such-key", Ct);
    }

    [Fact]
    public async Task Purging_an_app_removes_its_sessions_and_leaves_the_others()
    {
        // Task 3.6 purges on removal or a new client; the runbook in task 6.7 ends every session for
        // one app. Neither may touch another app's sessions.
        ISessionStore store = CreateStore();
        string app1 = await store.CreateAsync(Record(appId: "app1"), Ct);
        string alsoApp1 = await store.CreateAsync(Record(appId: "app1"), Ct);
        string app2 = await store.CreateAsync(Record(appId: "app2"), Ct);

        await store.RemoveAppAsync("app1", Ct);

        Assert.Null(await store.GetAsync(app1, Ct));
        Assert.Null(await store.GetAsync(alsoApp1, Ct));
        Assert.NotNull(await store.GetAsync(app2, Ct));
    }

    [Fact]
    public async Task Purging_an_app_with_no_sessions_is_not_an_error()
    {
        ISessionStore store = CreateStore();
        string other = await store.CreateAsync(Record(appId: "app2"), Ct);

        await store.RemoveAppAsync("app1", Ct);

        Assert.NotNull(await store.GetAsync(other, Ct));
    }

    [Fact]
    public async Task The_stored_ticket_bytes_survive_a_round_trip()
    {
        // SessionRecord.Ticket is bytes so the store never holds a live ticket object that two
        // requests could mutate (docs/plan/02-contracts.md -> Interfaces).
        ISessionStore store = CreateStore();
        byte[] ticket = [1, 2, 3, 250, 251, 252];
        string key = await store.CreateAsync(Record() with { Ticket = ticket }, Ct);

        SessionRecord? stored = await store.GetAsync(key, Ct);

        Assert.NotNull(stored);
        Assert.Equal(ticket, stored.Ticket.ToArray());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionRecord Record(
        string appId = "app1",
        string subject = "user-1",
        long version = 1) =>
        new(
            AppId: appId,
            ClientId: "1example23456789",
            Subject: subject,
            CreatedAt: new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero),
            ExpiresAt: new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero),
            Version: version,
            Ticket: new byte[] { 7, 7, 7 });
}
