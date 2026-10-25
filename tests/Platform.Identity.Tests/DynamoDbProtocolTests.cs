using System.Text;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// What the DynamoDB store and lease send, read off <see cref="FakeDynamoDbServer"/> as the SDK sent it:
/// each operation's target, keys, attributes and condition (change request P2-1, items 5 and 6, Decision
/// 7), a consistent read on every read, the 400 mapping, the SDK's treatment of a response CRC at the pin,
/// the read rules for an expired item and an unknown format, and what the table never holds.
/// </summary>
public sealed class DynamoDbProtocolTests : IAsyncLifetime
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
    public async Task A_create_is_a_PutItem_of_the_whole_item_under_attribute_not_exists()
    {
        // Mutation: drop the condition from the create's PutItem, and Condition is null.
        // Mutation: write expiresAt in milliseconds, and the time-to-live attribute reads 1000 times late.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));

        string key = await store.CreateAsync(Record, Ct);

        FakeDynamoDbServer.SeenRequest put = Assert.Single(fake.Requests);
        Assert.Equal("PutItem", put.Operation);
        Assert.Equal("application/x-amz-json-1.0", put.ContentType);
        Assert.Equal(FakeDynamoDbServer.Table, put.Body["TableName"]!.GetValue<string>());
        Assert.Equal("attribute_not_exists(pk)", put.Body["ConditionExpression"]!.GetValue<string>());
        JsonObject item = put.Body["Item"]!.AsObject();
        Assert.Equal(
            ["appId", "clientId", "createdAt", "expiresAt", "format", "pk", "recordExpiresAt", "subject", "ticket", "version"],
            item.Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.Equal(DynamoDbSessionTable.HashOf(key), S(item, "pk"));
        Assert.Equal("app1", S(item, "appId"));
        Assert.Equal("1example23456789", S(item, "clientId"));
        Assert.Equal("user-1", S(item, "subject"));
        Assert.Equal("2026-10-12T09:00:00.0000000Z", S(item, "createdAt"));
        Assert.Equal("2026-10-12T17:00:00.0000000Z", S(item, "recordExpiresAt"));
        Assert.Equal(TestClock.Instant.AddHours(8).ToUnixTimeSeconds().ToString(), N(item, "expiresAt"));
        Assert.Equal("1", N(item, "version"));
        Assert.Equal("1", N(item, "format"));
        Assert.NotNull(item["ticket"]!["B"]);
    }

    [Fact]
    public async Task Every_read_is_a_consistent_GetItem_by_pk_and_the_fake_refuses_any_other()
    {
        // The fake's refusal is what makes the store's flag observable: a GetItem without it fails.
        // Mutation: drop ConsistentRead from DynamoDbSessionTable.GetAsync, and every read here fails.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        string key = await store.CreateAsync(Record, Ct);

        Assert.NotNull(await store.GetAsync(key, Ct));
        await store.ReplaceAsync(key, Record with { Version = 2 }, expectedVersion: 99, Ct);

        IReadOnlyList<FakeDynamoDbServer.SeenRequest> reads = [.. fake.Requests.Where(r => r.Operation == "GetItem")];
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read =>
        {
            Assert.True(read.Body["ConsistentRead"]!.GetValue<bool>());
            Assert.Equal(DynamoDbSessionTable.HashOf(key), read.Body["Key"]!["pk"]!["S"]!.GetValue<string>());
        });

        using AmazonDynamoDBClient client = fake.Client();
        AmazonDynamoDBException refused = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => client.GetItemAsync(
            new GetItemRequest { TableName = FakeDynamoDbServer.Table, Key = new() { ["pk"] = new() { S = "x" } } }, Ct));
        Assert.Contains("ConsistentRead", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replace_is_a_PutItem_conditioned_on_existence_version_and_expiry()
    {
        // Mutation: drop "AND expiresAt > :now" from the replace, and the condition differs here (and an
        // expired record is replaced in An_expired_item_reads_as_absent_and_cannot_be_replaced).
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        string key = await store.CreateAsync(Record, Ct);

        Assert.True(await store.ReplaceAsync(key, Record with { Version = 2 }, expectedVersion: 1, Ct));

        FakeDynamoDbServer.SeenRequest replace = fake.Requests[^1];
        Assert.Equal("PutItem", replace.Operation);
        Assert.Equal("attribute_exists(pk) AND version = :expected AND expiresAt > :now", replace.Body["ConditionExpression"]!.GetValue<string>());
        Assert.Equal("1", replace.Body["ExpressionAttributeValues"]![":expected"]!["N"]!.GetValue<string>());
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds().ToString(), replace.Body["ExpressionAttributeValues"]![":now"]!["N"]!.GetValue<string>());
        Assert.Equal("2", N(replace.Body["Item"]!.AsObject(), "version"));
    }

    [Fact]
    public async Task A_remove_is_an_unconditional_DeleteItem_by_pk()
    {
        // Mutation: give the remove a condition (attribute_exists(pk)), and the request carries one.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        string key = await store.CreateAsync(Record, Ct);

        await store.RemoveAsync(key, Ct);

        FakeDynamoDbServer.SeenRequest delete = fake.Requests[^1];
        Assert.Equal("DeleteItem", delete.Operation);
        Assert.Null(delete.Body["ConditionExpression"]);
        Assert.Equal(DynamoDbSessionTable.HashOf(key), delete.Body["Key"]!["pk"]!["S"]!.GetValue<string>());
        Assert.Empty(fake.Items);
    }

    [Fact]
    public async Task A_purge_queries_the_appId_index_and_deletes_25_at_a_time()
    {
        // Mutation: chunk by 26, and the fake refuses the batch (25 is DynamoDB's limit); chunk by 1, and
        // there are 30 batches rather than 2.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        for (int i = 0; i < 30; i++)
        {
            await store.CreateAsync(Record, Ct);
        }

        string other = await store.CreateAsync(Record with { AppId = "app2" }, Ct);

        await store.RemoveAppAsync("app1", Ct);

        FakeDynamoDbServer.SeenRequest query = Assert.Single(fake.Requests, r => r.Operation == "Query");
        Assert.Equal("appId", query.Body["IndexName"]!.GetValue<string>());
        Assert.Equal("app1", query.Body["ExpressionAttributeValues"]![":app"]!["S"]!.GetValue<string>());
        IReadOnlyList<FakeDynamoDbServer.SeenRequest> batches = [.. fake.Requests.Where(r => r.Operation == "BatchWriteItem")];
        Assert.Equal([25, 5], batches.Select(b => b.Body["RequestItems"]![FakeDynamoDbServer.Table]!.AsArray().Count));
        Assert.Equal([DynamoDbSessionTable.HashOf(other)], fake.Items.Keys);
    }

    [Fact]
    public async Task A_lease_is_an_item_of_its_own_taken_and_released_under_its_conditions()
    {
        // Mutation: put the lease on the session's own pk, and pk lacks the lease# prefix.
        // Mutation: keep leaseUntil in seconds, and it is 1000 times too small.
        // Mutation: drop "OR leaseToken = :token", and the condition differs (and the dropped acquire of
        // DynamoDbWriteOutcomeTests is refused on its retry).
        FakeTimeProvider clock = TestClock.New();
        DynamoDbRefreshLeases leases = new(DynamoDbTables.Over(fake, clock));

        RefreshLease lease = (await leases.TryAcquireAsync("session-a", TimeSpan.FromSeconds(21), Ct))!;
        await leases.ReleaseAsync("session-a", lease, Ct);

        Assert.Equal(["PutItem", "DeleteItem"], fake.Operations);
        FakeDynamoDbServer.SeenRequest acquire = fake.Requests[0];
        JsonObject item = acquire.Body["Item"]!.AsObject();
        Assert.Equal(["expiresAt", "leaseToken", "leaseUntil", "pk"], item.Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.Equal("lease#" + DynamoDbSessionTable.HashOf("session-a"), S(item, "pk"));
        Assert.Equal(lease.Token, S(item, "leaseToken"));
        long until = TestClock.Instant.AddSeconds(21).ToUnixTimeMilliseconds();
        Assert.Equal(until.ToString(), N(item, "leaseUntil"));
        Assert.Equal((until / 1000).ToString(), N(item, "expiresAt"));
        Assert.Equal(
            "attribute_not_exists(pk) OR leaseUntil < :now OR leaseToken = :token",
            acquire.Body["ConditionExpression"]!.GetValue<string>());
        Assert.Equal(TestClock.Instant.ToUnixTimeMilliseconds().ToString(), acquire.Body["ExpressionAttributeValues"]![":now"]!["N"]!.GetValue<string>());

        FakeDynamoDbServer.SeenRequest release = fake.Requests[1];
        Assert.Equal("leaseToken = :token", release.Body["ConditionExpression"]!.GetValue<string>());
        Assert.Equal(lease.Token, release.Body["ExpressionAttributeValues"]![":token"]!["S"]!.GetValue<string>());
        Assert.Equal("lease#" + DynamoDbSessionTable.HashOf("session-a"), release.Body["Key"]!["pk"]!["S"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_condition_arrives_from_the_SDK_as_ConditionalCheckFailedException()
    {
        // The 400 with __type the fake sends is what the SDK maps to the exception the store reads as a
        // refusal; the SDK maps by __type, so the status alone does not decide it (a 500 naming the same
        // type still arrives as this exception, and is retried by the SDK's own policy). Mutation: send the
        // fake's error body without __type, and the SDK throws a bare AmazonDynamoDBException.
        using AmazonDynamoDBClient client = fake.Client();
        PutItemRequest put = new()
        {
            TableName = FakeDynamoDbServer.Table,
            Item = new() { ["pk"] = new() { S = "x" } },
            ConditionExpression = "attribute_not_exists(pk)",
        };
        await client.PutItemAsync(put, Ct);

        await Assert.ThrowsAsync<ConditionalCheckFailedException>(() => client.PutItemAsync(put, Ct));
    }

    [Fact]
    public async Task The_SDK_at_the_pin_checks_a_response_CRC_only_when_the_header_is_sent_and_the_fake_sends_none()
    {
        // Change request P2-1 Decision 6: the SDK checks x-amz-crc32 only when present, so the fake omits
        // it. Both halves at AWSSDK.DynamoDBv2 4.0.108: with no header the fake's answers are accepted (as
        // in every other test here), and a wrong one is refused, so the check exists and the fake's
        // omission is what keeps it off. Mutation: make the fake send a wrong x-amz-crc32 by default, and
        // the first read below fails with the SDK's CRC IOException.
        using AmazonDynamoDBClient client = fake.Client();
        GetItemRequest read = new() { TableName = FakeDynamoDbServer.Table, Key = new() { ["pk"] = new() { S = "x" } }, ConsistentRead = true };

        await client.GetItemAsync(read, Ct);
        Assert.Null(fake.Crc32);

        fake.Crc32 = "1";
        IOException refused = await Assert.ThrowsAsync<IOException>(() => client.GetItemAsync(read, Ct));
        Assert.Contains("CRC", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_item_reads_as_absent_and_cannot_be_replaced()
    {
        // The fake has no sweeper, as the real table's is slow, so the item is still there: absence is the
        // store's own rule, expiresAt <= now. Mutation: read expiresAt < now, and the read at the instant
        // of expiry returns the record; drop the replace's expiry clause, and the replace succeeds.
        FakeTimeProvider clock = TestClock.New();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, clock));
        string key = await store.CreateAsync(Record, Ct);

        clock.Advance(TimeSpan.FromHours(8));

        Assert.Null(await store.GetAsync(key, Ct));
        Assert.False(await store.ReplaceAsync(key, Record with { Version = 2 }, expectedVersion: 1, Ct));
        Assert.Single(fake.Items);
    }

    [Fact]
    public async Task An_item_in_an_unknown_newer_format_reads_as_absent_and_is_logged_once()
    {
        // MR-9's rolled-back image. Mutation: read format 2 as format 1, and the record comes back; log on
        // every read, and there are two lines.
        CapturedLog log = new();
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New(), log.CreateLogger<DynamoDbSessionStore>()));
        string key = await store.CreateAsync(Record, Ct);
        string second = await store.CreateAsync(Record, Ct);
        foreach (JsonObject item in fake.Items.Values)
        {
            item["format"] = new JsonObject { ["N"] = "2" };
            fake.Plant(item);
        }

        Assert.Null(await store.GetAsync(key, Ct));
        Assert.Null(await store.GetAsync(second, Ct));

        CapturedLine line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("format 2", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_holds_no_session_key_no_plaintext_ticket_and_no_token()
    {
        // The ticket is the serialised ticket with its saved tokens; a canary stands in for a token.
        // Mutation: store record.Ticket unprotected, and the canary's bytes are in the table.
        // Mutation: key the item by the session key itself, and the key is in the table.
        const string canary = "canary-access-token-5f1c9e";
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        byte[] ticket = Encoding.UTF8.GetBytes("ticket{" + canary + "}");

        string key = await store.CreateAsync(Record with { Ticket = ticket }, Ct);

        string table = string.Join("\n", fake.Requests.Select(r => r.Body.ToJsonString()));
        string pk = Assert.Single(fake.Items.Keys);
        byte[] stored = Convert.FromBase64String(fake.Items[pk]["ticket"]!["B"]!.GetValue<string>());
        Assert.DoesNotContain(key, table, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, table, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(ticket), table, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Equal(SessionRef.Of(key), pk[..SessionRef.Length]);
        Assert.Equal(ticket, (await store.GetAsync(key, Ct))!.Ticket.ToArray());
    }

    [Fact]
    public async Task A_ticket_is_bound_to_its_app_by_the_protection_purpose()
    {
        // The second purpose is the app id. Mutation: protect under the store's purpose alone, and a ticket
        // moved onto another app's item unprotects there.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        string key = await store.CreateAsync(Record, Ct);
        JsonObject item = fake.Items[DynamoDbSessionTable.HashOf(key)];
        item["appId"] = new JsonObject { ["S"] = "app2" };
        fake.Plant(item);

        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public async Task A_cancellation_after_send_reaches_the_table_and_the_SDK_reports_only_that_it_was_cancelled()
    {
        // The first fact P2-1 Decision 8 asks for, shown on the SDK's own client, not through the store:
        // the write is held after it has committed, the token is cancelled, and the SDK throws
        // TaskCanceledException, makes no second attempt, and says nothing of the commit.
        // Mutation (of the fact): were the SDK to report a commit or retry, the type or the count differs.
        using AmazonDynamoDBClient client = fake.Client(maxErrorRetry: null);
        fake.Next("PutItem", FakeDynamoDbServer.Fault.HoldAfterCommit);
        using CancellationTokenSource caller = new();
        Task put = client.PutItemAsync(
            new PutItemRequest { TableName = FakeDynamoDbServer.Table, Item = new() { ["pk"] = new() { S = "x" } } }, caller.Token);
        await fake.Held.Task.WaitAsync(Ct);

        await caller.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => put);
        Assert.Equal(["PutItem"], fake.Operations);
        Assert.True(fake.Items.ContainsKey("x"));
    }

    [Fact]
    public async Task The_store_never_hands_the_callers_token_to_the_SDK_so_a_cancellation_after_send_drops_no_write()
    {
        // The second fact: how the store treats it. The caller's token is read before anything is sent and
        // not after (T-B17), so the create held after its commit completes when released, and returns its key.
        // Mutation: pass the caller's token to PutItemAsync, and the create throws TaskCanceledException.
        DynamoDbSessionStore store = new(DynamoDbTables.Over(fake, TestClock.New()));
        fake.Next("PutItem", FakeDynamoDbServer.Fault.HoldAfterCommit);
        using CancellationTokenSource caller = new();
        Task<string> create = store.CreateAsync(Record, caller.Token);
        await fake.Held.Task.WaitAsync(Ct);

        await caller.CancelAsync();
        fake.Release.TrySetResult();

        string key = await create.WaitAsync(Ct);
        Assert.Equal(["PutItem"], fake.Operations);
        Assert.NotNull(await store.GetAsync(key, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string S(JsonObject item, string name) => item[name]!["S"]!.GetValue<string>();

    private static string N(JsonObject item, string name) => item[name]!["N"]!.GetValue<string>();
}
