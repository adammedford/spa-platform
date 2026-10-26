using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;
using Fault = Platform.Identity.Tests.FakeDynamoDbServer.Fault;

namespace Platform.Identity.Tests;

/// <summary>
/// The purge by app over <see cref="FakeDynamoDbServer"/> where the table answers in parts (#391; #344
/// review X5 and X11): a <c>Query</c> that pages through <c>LastEvaluatedKey</c>, and a
/// <c>BatchWriteItem</c> that hands some of its deletes back as <c>UnprocessedItems</c>, which the store
/// sends again after a growing wait and gives up on at <c>StoreDeadline</c>.
/// </summary>
public sealed class DynamoDbPurgeTests : IAsyncLifetime
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
    public async Task A_purge_follows_LastEvaluatedKey_until_the_index_has_no_more_pages()
    {
        // X11. A Query answers at most 1 MB, so a large app's keys arrive over several pages
        // (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.Pagination.html).
        // Mutation: stop after the first page (start = null), and one Query is sent and 10 sessions stay.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        for (int i = 0; i < 30; i++)
        {
            await store.CreateAsync(Record, Ct);
        }

        string other = await store.CreateAsync(Record with { AppId = "app2" }, Ct);
        fake.QueryPageSize = 20;

        await store.RemoveAppAsync("app1", Ct);

        IReadOnlyList<FakeDynamoDbServer.SeenRequest> queries = [.. fake.Requests.Where(r => r.Operation == "Query")];
        Assert.Equal(2, queries.Count);
        Assert.Null(queries[0].Body["ExclusiveStartKey"]);
        JsonObject resumedAt = queries[1].Body["ExclusiveStartKey"]!.AsObject();
        Assert.Equal("app1", resumedAt["appId"]!["S"]!.GetValue<string>());
        IReadOnlyList<FakeDynamoDbServer.SeenRequest> batches = Batches();
        Assert.Equal([20, 10], batches.Select(Size));

        // The second page resumed at the last key the first page deleted.
        Assert.Equal(resumedAt["pk"]!["S"]!.GetValue<string>(), PkAt(batches[0], Size(batches[0]) - 1));
        Assert.Equal([DynamoDbSessionTable.HashOf(other)], fake.Items.Keys);
    }

    [Fact]
    public async Task A_delete_left_unprocessed_is_sent_again_alone_after_a_wait()
    {
        // X5. The table answers a throttled part of a batch as UnprocessedItems, unapplied, and AWS
        // recommends sending it again after an exponential backoff
        // (https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html).
        // Mutation: drop the unprocessed items (pending = []), and one batch is sent and a session stays.
        // Mutation: resend the whole batch, and the second batch carries 5.
        // Mutation: resend at once, and the second batch arrives before the clock has moved 50 ms.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        for (int i = 0; i < 5; i++)
        {
            await store.CreateAsync(Record, Ct);
        }

        fake.Next("BatchWriteItem", Fault.LeaveLastUnprocessed);

        Task purge = store.RemoveAppAsync("app1", Ct);
        TimeSpan untilResent = await AdvanceUntil(clock, () => Batches().Count >= 2 || purge.IsCompleted);
        await purge.WaitAsync(Ct);

        IReadOnlyList<FakeDynamoDbServer.SeenRequest> batches = Batches();
        Assert.Equal([5, 1], batches.Select(Size));
        Assert.Equal(PkAt(batches[0], 4), PkAt(batches[1], 0));
        Assert.Empty(fake.Items);
        Assert.True(untilResent >= FirstWait, $"The unprocessed delete was sent again after {untilResent.TotalMilliseconds} ms on the store's clock.");
    }

    [Fact]
    public async Task A_batch_whose_send_ended_unknown_is_sent_once_more_after_a_wait()
    {
        // A batch whose send failed or whose answer was lost is sent once more, a delete being idempotent,
        // and that resend waits as an unprocessed one does (#391's Answer, "Decided by implementer").
        // Mutation: skip the wait once a send has ended unknown (resend > 0 && !retried), and the second
        // batch arrives before the clock has moved 50 ms.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        await store.CreateAsync(Record, Ct);
        fake.Next("BatchWriteItem", Fault.FailBeforeCommit);

        Task purge = store.RemoveAppAsync("app1", Ct);
        TimeSpan untilResent = await AdvanceUntil(clock, () => Batches().Count >= 2 || purge.IsCompleted);
        await purge.WaitAsync(Ct);

        Assert.Equal([1, 1], Batches().Select(Size));
        Assert.Empty(fake.Items);
        Assert.True(untilResent >= FirstWait, $"The batch was sent again after {untilResent.TotalMilliseconds} ms on the store's clock.");
    }

    [Fact]
    public async Task Each_further_resend_of_a_batch_waits_twice_as_long_as_the_one_before()
    {
        // X5's backoff is exponential: 50, 100 and 200 ms before the second, third and fourth sends, so the
        // fourth arrives no sooner than 350 ms after the purge began, on the store's clock.
        // Mutation: wait 50 ms every time, and the fourth send arrives after about 150 ms.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        await store.CreateAsync(Record, Ct);
        for (int i = 0; i < 3; i++)
        {
            fake.Next("BatchWriteItem", Fault.LeaveLastUnprocessed);
        }

        Task purge = store.RemoveAppAsync("app1", Ct);
        TimeSpan untilSecond = await AdvanceUntil(clock, () => Batches().Count >= 2 || purge.IsCompleted);
        TimeSpan untilThird = untilSecond + await AdvanceUntil(clock, () => Batches().Count >= 3 || purge.IsCompleted);
        TimeSpan untilFourth = untilThird + await AdvanceUntil(clock, () => Batches().Count >= 4 || purge.IsCompleted);
        await purge.WaitAsync(Ct);

        Assert.Equal([1, 1, 1, 1], Batches().Select(Size));
        Assert.Empty(fake.Items);
        Assert.True(untilSecond >= FirstWait, $"The second send came {untilSecond.TotalMilliseconds} ms in.");
        Assert.True(untilThird >= FirstWait * 3, $"The third send came {untilThird.TotalMilliseconds} ms in.");
        Assert.True(untilFourth >= FirstWait * 7, $"The fourth send came {untilFourth.TotalMilliseconds} ms in.");
    }

    [Fact]
    public async Task A_batch_the_table_keeps_leaving_unprocessed_fails_the_purge_at_StoreDeadline()
    {
        // The waits run inside the batch's own deadline, and a deadline that passes during one fails the
        // purge as the store's own exception, which OptionsInvalidator logs as PurgeFailed.
        // Mutation: let the wait's cancellation escape, and a TaskCanceledException arrives instead.
        // Mutation: resend at once, and the fake's 50 refusals run out within the deadline and the purge
        // returns as if it had finished.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        string key = await store.CreateAsync(Record, Ct);
        for (int i = 0; i < 50; i++)
        {
            fake.Next("BatchWriteItem", Fault.LeaveLastUnprocessed);
        }

        Task purge = store.RemoveAppAsync("app1", Ct);
        TimeSpan untilDone = await AdvanceUntil(clock, () => purge.IsCompleted, TimeSpan.FromMilliseconds(10));

        SessionStoreUnavailableException failed = await Assert.ThrowsAsync<SessionStoreUnavailableException>(() => purge.WaitAsync(Ct));
        Assert.Contains("purge by app (Query, BatchWriteItem)", failed.Message, StringComparison.Ordinal);
        Assert.True(untilDone >= DynamoDbSessionTable.DefaultStoreDeadline, $"The purge gave up {untilDone.TotalMilliseconds} ms in.");
        Assert.InRange(Batches().Count, 2, 12);
        Assert.Equal([DynamoDbSessionTable.HashOf(key)], fake.Items.Keys);
    }

    /// <summary>
    /// The first wait before unprocessed items are sent again, as #391's Answer sets it: a literal here, not
    /// the store's constant, so a store that stops waiting cannot also move what this test expects.
    /// </summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Moves <paramref name="clock"/> by <paramref name="step"/> (a millisecond unless given), letting the store and the fake run between
    /// steps, until <paramref name="done"/> holds, and answers how far the clock moved. The store's wait
    /// starts no earlier than the request before it reached the fake, so the clock has moved at least
    /// that wait by the time the next request arrives: a lower bound however late the store's timer was
    /// set. Fails, rather than spinning, once the clock has moved further than any purge here can need.
    /// </summary>
    private static async Task<TimeSpan> AdvanceUntil(FakeTimeProvider clock, Func<bool> done, TimeSpan? step = null)
    {
        TimeSpan by = step ?? TimeSpan.FromMilliseconds(1);
        TimeSpan advanced = TimeSpan.Zero;
        while (!done())
        {
            Assert.True(advanced < TimeSpan.FromSeconds(20), "The purge neither finished nor sent what the test waited for in 20 s on its clock.");
            clock.Advance(by);
            advanced += by;
            await Task.Delay(1, Ct);
        }

        return advanced;
    }

    private IReadOnlyList<FakeDynamoDbServer.SeenRequest> Batches() => [.. fake.Requests.Where(r => r.Operation == "BatchWriteItem")];

    private static string PkAt(FakeDynamoDbServer.SeenRequest batch, int index) =>
        batch.Body["RequestItems"]![FakeDynamoDbServer.Table]!.AsArray()[index]!["DeleteRequest"]!["Key"]!["pk"]!["S"]!.GetValue<string>();

    private static int Size(FakeDynamoDbServer.SeenRequest batch) =>
        batch.Body["RequestItems"]![FakeDynamoDbServer.Table]!.AsArray().Count;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
