using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;
using Fault = Platform.Identity.Tests.FakeDynamoDbServer.Fault;

namespace Platform.Identity.Tests;

/// <summary>
/// D4's schedules (#332; amends change request P2-1 Decision 8), over <see cref="FakeDynamoDbServer"/>:
/// a write whose response is lost after it committed, one that fails before it commits, and one still
/// unknown when <c>StoreDeadline</c> runs out, for create, remove and the lease's acquire (F10, F11, F12).
/// The SDK's own retries are off on these clients, so each request the fake counts is the store's own,
/// except in the one test that keeps the SDK's default to show a client from the container composes.
/// </summary>
public sealed class DynamoDbWriteOutcomeTests : IAsyncLifetime
{
    private static readonly SessionRecord Record = new(
        AppId: "app1",
        ClientId: "1example23456789",
        Subject: "user-1",
        CreatedAt: TestClock.Instant,
        ExpiresAt: TestClock.Instant.AddHours(8),
        Version: 1,
        Ticket: new byte[] { 7, 7, 7 });

    private FakeDynamoDbServer fake = null!;

    public async ValueTask InitializeAsync() => fake = await FakeDynamoDbServer.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await fake.DisposeAsync();

    [Fact]
    public async Task A_create_whose_response_is_lost_after_it_committed_reads_back_as_success()
    {
        // Mutation: answer an unknown create with the defined failure instead of re-reading, and this
        // throws.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        fake.Next("PutItem", Fault.CommitThenDrop);

        string key = await store.CreateAsync(Record, Ct);

        Assert.Equal(["PutItem", "GetItem"], fake.Operations);
        Assert.Equal([DynamoDbSessionTable.HashOf(key)], fake.Items.Keys);
        Assert.Equal("user-1", (await store.GetAsync(key, Ct))!.Subject);
    }

    [Fact]
    public async Task A_create_that_fails_before_it_commits_retries_once_under_the_same_key_and_succeeds()
    {
        // Mutation: retry under a new key, and the two PutItems carry different pks; make no retry, and the
        // create throws.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        fake.Next("PutItem", Fault.FailBeforeCommit);

        string key = await store.CreateAsync(Record, Ct);

        Assert.Equal(["PutItem", "GetItem", "PutItem"], fake.Operations);
        IReadOnlyList<string> pks = [.. fake.Requests.Where(r => r.Operation == "PutItem").Select(r => r.Body["Item"]!["pk"]!["S"]!.GetValue<string>())];
        Assert.Equal([DynamoDbSessionTable.HashOf(key), DynamoDbSessionTable.HashOf(key)], pks);
        Assert.NotNull(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_create_whose_retry_meets_its_own_late_commit_is_a_success()
    {
        // D4: the retry's condition failure is our own commit. The first PutItem answers 500 and lands only
        // after the store's re-read has found nothing, so the retry under the same key meets it. Mutation:
        // read the retry's condition failure as a failure, and this create throws.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        fake.Next("PutItem", Fault.CommitAfterNextRead);

        string key = await store.CreateAsync(Record, Ct);

        Assert.Equal(["PutItem", "GetItem", "PutItem"], fake.Operations);
        Assert.NotNull(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_client_with_the_SDKs_own_retries_reads_its_resend_of_a_committed_create_as_success()
    {
        // A client from the container keeps the SDK's retries (10 by default for DynamoDB, cited in
        // DynamoDbSessionTable's summary). Its resend of a create whose response was lost fails
        // attribute_not_exists against the create's own commit, and the store re-reads and finds its own
        // ciphertext. Mutation: read a first-send condition failure as a collision without re-reading, and
        // this create throws.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New(), maxErrorRetry: null));
        fake.Next("PutItem", Fault.CommitThenDrop);

        string key = await store.CreateAsync(Record, Ct);

        Assert.Equal(["PutItem", "PutItem", "GetItem"], fake.Operations);
        Assert.NotNull(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_create_that_collides_with_another_item_fails_rather_than_overwriting()
    {
        // Why the condition is there at all (change request P2-1, item 5): a key already handed out fails
        // loudly. The fake plants another writer's item under the create's own pk just before it, and only
        // the re-read's compare tells that from our own commit. Mutation: read every first-send condition
        // failure as success, and this create returns a key naming another writer's session.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        fake.Next("PutItem", Fault.ForeignItemFirst);

        await Assert.ThrowsAsync<SessionStoreUnavailableException>(() => store.CreateAsync(Record, Ct));

        Assert.Equal(["PutItem", "GetItem"], fake.Operations);
        Assert.Equal("another writer", Assert.Single(fake.Items.Values)["foreign"]!["S"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_create_still_unknown_at_StoreDeadline_fails_with_no_key_returned()
    {
        // The write is held, uncommitted, past the deadline: no re-read or retry fits, so the outcome is
        // unknown and the login fails with no cookie. Mutation: return the key on an unknown outcome, and
        // this returns one; bound only the first send by the deadline, and a re-read runs after it.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        fake.Next("PutItem", Fault.HoldBeforeCommit);

        Task<string> create = store.CreateAsync(Record, Ct);
        await fake.Held.Task.WaitAsync(Ct);
        clock.Advance(DynamoDbSessionTable.DefaultStoreDeadline);

        SessionStoreUnavailableException failed = await Assert.ThrowsAsync<SessionStoreUnavailableException>(() => create.WaitAsync(Ct));
        Assert.Contains("create (PutItem)", failed.Message, StringComparison.Ordinal);
        Assert.Equal(["PutItem"], fake.Operations);
        Assert.Empty(fake.Items);
    }

    [Fact]
    public async Task A_remove_whose_response_is_lost_retries_once_and_succeeds()
    {
        // Mutation: make no retry, and the remove throws "not confirmed".
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        string key = await store.CreateAsync(Record, Ct);
        fake.Next("DeleteItem", Fault.CommitThenDrop);

        await store.RemoveAsync(key, Ct);

        Assert.Equal(["PutItem", "DeleteItem", "DeleteItem"], fake.Operations);
        Assert.Empty(fake.Items);
    }

    [Fact]
    public async Task A_remove_still_unknown_at_StoreDeadline_is_not_confirmed()
    {
        // F11: the store reports the remove not confirmed by throwing; it does not claim a removal it cannot
        // show. Mutation: swallow the unknown outcome, and the remove returns as if it had removed.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        string key = await store.CreateAsync(Record, Ct);
        fake.Next("DeleteItem", Fault.HoldBeforeCommit);

        Task remove = store.RemoveAsync(key, Ct);
        await fake.Held.Task.WaitAsync(Ct);
        clock.Advance(DynamoDbSessionTable.DefaultStoreDeadline);

        SessionStoreUnavailableException failed = await Assert.ThrowsAsync<SessionStoreUnavailableException>(() => remove.WaitAsync(Ct));
        Assert.Contains("remove (DeleteItem)", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replace_whose_response_is_lost_after_it_committed_reads_as_success()
    {
        // A replace runs under the session gate with no request token, but its response can still be lost.
        // Mutation: read an unknown replace as refused, and this is false while the table holds version 2.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        string key = await store.CreateAsync(Record, Ct);
        fake.Next("PutItem", Fault.CommitThenDrop);

        Assert.True(await store.ReplaceAsync(key, Record with { Version = 2 }, expectedVersion: 1, Ct));
        Assert.Equal(2, (await store.GetAsync(key, Ct))!.Version);
    }

    [Fact]
    public async Task A_client_with_the_SDKs_own_retries_reads_its_resend_of_a_committed_replace_as_success()
    {
        // The implementer's Decision 2, for replace: the SDK's resend of a replace whose response was lost
        // fails "version = :expected" against the replace's own commit, and the store re-reads and finds its
        // own ciphertext. Mutation: read a first-send condition failure as refused without re-reading, and
        // this is false while the table holds version 2.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New(), maxErrorRetry: null));
        string key = await store.CreateAsync(Record, Ct);
        fake.Next("PutItem", Fault.CommitThenDrop);

        Assert.True(await store.ReplaceAsync(key, Record with { Version = 2 }, expectedVersion: 1, Ct));
        Assert.Equal(["PutItem", "PutItem", "PutItem", "GetItem"], fake.Operations);
        Assert.Equal(2, (await store.GetAsync(key, Ct))!.Version);
    }

    [Fact]
    public async Task A_lease_acquire_whose_success_is_lost_reacquires_with_the_same_token_and_leaseUntil()
    {
        // F12, inside one TryAcquireAsync (G3). Mutation: build a fresh token for the retry, and the retry is
        // refused by the lease the first send took (the call answers null); drop "OR leaseToken = :token",
        // and the same.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbRefreshLeases leases = new(DynamoDbTables.Over(fake, clock));
        fake.Next("PutItem", Fault.CommitThenDrop);

        RefreshLease? lease = await leases.TryAcquireAsync("session-a", TimeSpan.FromSeconds(21), Ct);

        Assert.NotNull(lease);
        IReadOnlyList<JsonObject> sends = [.. fake.Requests.Select(r => r.Body)];
        Assert.Equal(["PutItem", "PutItem"], fake.Operations);
        Assert.Equal(sends[0]["Item"]!["leaseToken"]!.ToJsonString(), sends[1]["Item"]!["leaseToken"]!.ToJsonString());
        Assert.Equal(sends[0]["Item"]!["leaseUntil"]!.ToJsonString(), sends[1]["Item"]!["leaseUntil"]!.ToJsonString());
        Assert.Equal(lease.Token, sends[1]["Item"]!["leaseToken"]!["S"]!.GetValue<string>());
        Assert.Equal(TestClock.Instant.AddSeconds(21), lease.ExpiresAt);
        Assert.Null(await leases.TryAcquireAsync("session-a", TimeSpan.FromSeconds(21), Ct));
    }

    [Fact]
    public async Task A_lease_success_that_arrives_after_the_leases_own_expiry_confers_nothing()
    {
        // D4: a late success confers nothing. The lease is 2 s, inside StoreDeadline's 5 s, and its success
        // is held until 3 s have passed. Mutation: return the lease on any success, and this is not null.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbRefreshLeases leases = new(DynamoDbTables.Over(fake, clock));
        fake.Next("PutItem", Fault.HoldAfterCommit);

        Task<RefreshLease?> acquire = leases.TryAcquireAsync("session-a", TimeSpan.FromSeconds(2), Ct);
        await fake.Held.Task.WaitAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(3));
        fake.Release.TrySetResult();

        Assert.Null(await acquire.WaitAsync(Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
