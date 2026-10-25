using System.Globalization;
using System.Security.Cryptography;
using Amazon.DynamoDBv2.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using static Platform.Identity.DynamoDbSessionTable;

namespace Platform.Identity;

/// <summary>
/// The Phase 2 <see cref="ISessionStore"/> on DynamoDB (plan row P2-1; change request P2-1, Decision 1
/// to 9; #332 D4 and F10, F11, F13, F17). It passes <c>Platform.Testing.SessionStoreContract</c>, as the
/// memory store does, so a serialised ticket reads back on any task.
///
/// <para><b>The item</b> (P2-1 item 6): <c>pk</c>, the hash of the session key
/// (<see cref="DynamoDbSessionTable.HashOf"/>); <c>appId</c>, <c>clientId</c>, <c>subject</c>;
/// <c>createdAt</c> and <c>recordExpiresAt</c> in the round-trip format, so the record reads back equal;
/// <c>expiresAt</c> in epoch seconds, the table's time-to-live attribute; <c>version</c>; <c>ticket</c>,
/// the serialised ticket protected by data protection under <see cref="Purpose"/> and the app id; and
/// <c>format</c>, <see cref="Format"/>. The serialised ticket holds the tokens, so it never reaches the
/// table in the clear: data protection is the envelope the plan row names.</para>
///
/// <para><b>Expiry.</b> The table's sweeper is slow (expired items are typically deleted within a few
/// days and can be read until then: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html),
/// so <c>expiresAt &lt;= now</c> reads as absent, one rule for the read and the replace, both compared
/// in whole epoch seconds. <c>expiresAt</c> is the expiry rounded down, so a session ends up to a second
/// before its <see cref="SessionRecord.ExpiresAt"/>, never after it.</para>
///
/// <para><b>No in-process cache</b> of any kind: a session ended through one task is refused by the
/// other on its next request only because every read reads the table (P2-1 item 6; P2-3).</para>
///
/// <para><b>Write outcomes</b> (D4). Create: an unknown outcome is followed by a consistent re-read;
/// found is success, not found is one retry under the same key, whose condition failure is our own
/// commit; still unknown at the deadline, the create fails and no key is returned, so no cookie is
/// issued. Replace: a condition failure is re-read, so an SDK retry of a write that committed reads as
/// the success it was. Remove: one retry, since a delete is idempotent; unknown at the deadline is "not
/// confirmed", and the store throws. Read: one retry.</para>
/// </summary>
internal sealed class DynamoDbSessionStore(DynamoDbSessionTable table) : ISessionStore
{
    /// <summary>The item's shape version (MR-9). A build reads its own format and the one before it.</summary>
    internal const int Format = 1;

    /// <summary>The data-protection purpose of the ticket's envelope; the app id is the second purpose.</summary>
    internal const string Purpose = "Platform.Identity.SessionTicket";

    private const string Create = "create (PutItem)";
    private const string Read = "read (GetItem)";
    private const string Replace = "replace (PutItem)";
    private const string Remove = "remove (DeleteItem)";
    private const string Purge = "purge by app (Query, BatchWriteItem)";

    /// <summary>A BatchWriteItem carries at most 25 requests (https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_BatchWriteItem.html).</summary>
    private const int BatchSize = 25;

    private int formatLogged;
    private int unprotectLogged;

    public async Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        // 256 bits from the framework's CSPRNG, as the memory store issues them: the key is the only thing
        // the session cookie carries, so it is a credential, and a collision is not a case to handle.
        string key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string pk = HashOf(key);
        Dictionary<string, AttributeValue> item = Item(pk, record);

        using CancellationTokenSource deadline = table.Deadline();
        Attempt<PutItemResponse> first = await TryAsync(d => Put(item, "attribute_not_exists(pk)", null, d), deadline.Token);
        if (first.Outcome == Outcome.Done)
        {
            return key;
        }

        // A condition failure on the first send is the SDK's own retry of a write that had committed, or
        // a collision. The re-read tells them apart: only our own write holds our ciphertext.
        Attempt<GetItemResponse> reread = await TryAsync(d => table.GetAsync(pk, d), deadline.Token);
        if (reread.Outcome == Outcome.Done && SameItem(reread.Value!.Item, item))
        {
            return key;
        }

        if (first.Outcome == Outcome.ConditionFailed || reread.Outcome != Outcome.Done || reread.Value!.Item is { Count: > 0 })
        {
            throw new SessionStoreUnavailableException(Create, table.StoreDeadline, first.Error ?? reread.Error);
        }

        // Not found: the first send never committed. One retry under the same key; its condition failure
        // is our own commit, since nothing else writes under a fresh 256-bit key (D4).
        Attempt<PutItemResponse> retry = await TryAsync(d => Put(item, "attribute_not_exists(pk)", null, d), deadline.Token);
        return retry.Outcome != Outcome.Unknown
            ? key
            : throw new SessionStoreUnavailableException(Create, table.StoreDeadline, retry.Error);
    }

    public async Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ct.ThrowIfCancellationRequested();
        string pk = HashOf(key);

        using CancellationTokenSource deadline = table.Deadline();
        Attempt<GetItemResponse> read = await TryAsync(d => table.GetAsync(pk, d), deadline.Token);
        if (read.Outcome != Outcome.Done)
        {
            read = await TryAsync(d => table.GetAsync(pk, d), deadline.Token);
        }

        return read.Outcome == Outcome.Done
            ? Live(read.Value!.Item)
            : throw new SessionStoreUnavailableException(Read, table.StoreDeadline, read.Error);
    }

    public async Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        string pk = HashOf(key);
        Dictionary<string, AttributeValue> item = Item(pk, record);

        // The same rule as the read: a record past its expiry is absent, so it cannot be replaced.
        Dictionary<string, AttributeValue> values = new()
        {
            [":expected"] = new() { N = expectedVersion.ToString(CultureInfo.InvariantCulture) },
            [":now"] = new() { N = NowSeconds().ToString(CultureInfo.InvariantCulture) },
        };
        const string condition = "attribute_exists(pk) AND version = :expected AND expiresAt > :now";

        using CancellationTokenSource deadline = table.Deadline();
        Attempt<PutItemResponse> first = await TryAsync(d => Put(item, condition, values, d), deadline.Token);
        if (first.Outcome == Outcome.Done)
        {
            return true;
        }

        // A refusal, or an unknown outcome: read what the table holds now. Our own ciphertext means the
        // write committed (an SDK retry of it is what was refused, or its response was lost).
        Attempt<GetItemResponse> reread = await TryAsync(d => table.GetAsync(pk, d), deadline.Token);
        if (reread.Outcome == Outcome.Done && SameItem(reread.Value!.Item, item))
        {
            return true;
        }

        if (first.Outcome == Outcome.ConditionFailed)
        {
            return false;
        }

        if (reread.Outcome != Outcome.Done)
        {
            throw new SessionStoreUnavailableException(Replace, table.StoreDeadline, first.Error ?? reread.Error);
        }

        // Not ours: the first send never committed, so its one retry decides.
        Attempt<PutItemResponse> retry = await TryAsync(d => Put(item, condition, values, d), deadline.Token);
        return retry.Outcome switch
        {
            Outcome.Done => true,
            Outcome.ConditionFailed => false,
            _ => throw new SessionStoreUnavailableException(Replace, table.StoreDeadline, retry.Error),
        };
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ct.ThrowIfCancellationRequested();
        string pk = HashOf(key);

        using CancellationTokenSource deadline = table.Deadline();
        await DeleteAsync(pk, deadline.Token, Remove);
    }

    public async Task RemoveAppAsync(string appId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ct.ThrowIfCancellationRequested();

        // The index is keys only and eventually consistent, so a session created in the same second may
        // be missed; correctness does not rest on the purge, because AppSessions.OwnedBy already reads a
        // record bound to a client the app has left as absent (change request P2-1, item 5). Each page
        // and each batch is one operation under its own StoreDeadline, so a large purge is bounded per
        // call, not as a whole.
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            QueryRequest query = new()
            {
                TableName = table.TableName,
                IndexName = AppIndex,
                KeyConditionExpression = "appId = :app",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":app"] = new() { S = appId } },
                ExclusiveStartKey = start,
            };

            QueryResponse page;
            using (CancellationTokenSource deadline = table.Deadline())
            {
                Attempt<QueryResponse> read = await TryAsync(d => table.Client.QueryAsync(query, d), deadline.Token);
                if (read.Outcome != Outcome.Done)
                {
                    read = await TryAsync(d => table.Client.QueryAsync(query, d), deadline.Token);
                }

                page = read.Outcome == Outcome.Done
                    ? read.Value!
                    : throw new SessionStoreUnavailableException(Purge, table.StoreDeadline, read.Error);
            }

            foreach (Dictionary<string, AttributeValue>[] chunk in (page.Items ?? []).Chunk(BatchSize))
            {
                await DeleteBatchAsync([.. chunk.Select(found => found["pk"].S)]);
            }

            start = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (start is not null);
    }

    /// <summary>
    /// One unconditional delete with one retry: a delete is idempotent, so repeating one whose response
    /// was lost is safe (D4, F11). Unknown after the retry is "not confirmed".
    /// </summary>
    private async Task DeleteAsync(string pk, CancellationToken deadline, string operation)
    {
        DeleteItemRequest delete = new()
        {
            TableName = table.TableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = pk } },
        };

        Attempt<DeleteItemResponse> first = await TryAsync(d => table.Client.DeleteItemAsync(delete, d), deadline);
        if (first.Outcome == Outcome.Done)
        {
            return;
        }

        Attempt<DeleteItemResponse> retry = await TryAsync(d => table.Client.DeleteItemAsync(delete, d), deadline);
        if (retry.Outcome != Outcome.Done)
        {
            throw new SessionStoreUnavailableException(operation, table.StoreDeadline, retry.Error);
        }
    }

    /// <summary>
    /// Up to 25 deletes in one BatchWriteItem, sending again whatever the table hands back unprocessed
    /// until none is left or the deadline passes. A delete is idempotent, so a batch whose response was
    /// lost is sent once more.
    /// </summary>
    private async Task DeleteBatchAsync(string[] pks)
    {
        List<WriteRequest> pending = [.. pks.Select(pk => new WriteRequest
        {
            DeleteRequest = new DeleteRequest { Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = pk } } },
        })];

        using CancellationTokenSource deadline = table.Deadline();
        bool retried = false;
        while (pending.Count > 0)
        {
            BatchWriteItemRequest batch = new()
            {
                RequestItems = new Dictionary<string, List<WriteRequest>> { [table.TableName] = pending },
            };

            Attempt<BatchWriteItemResponse> sent = await TryAsync(d => table.Client.BatchWriteItemAsync(batch, d), deadline.Token);
            if (sent.Outcome != Outcome.Done)
            {
                if (retried || deadline.IsCancellationRequested)
                {
                    throw new SessionStoreUnavailableException(Purge, table.StoreDeadline, sent.Error);
                }

                retried = true;
                continue;
            }

            pending = sent.Value!.UnprocessedItems is { } left && left.TryGetValue(table.TableName, out List<WriteRequest>? again)
                ? again ?? []
                : [];
            if (pending.Count > 0 && deadline.IsCancellationRequested)
            {
                throw new SessionStoreUnavailableException(Purge, table.StoreDeadline, null);
            }
        }
    }

    private Task<PutItemResponse> Put(
        Dictionary<string, AttributeValue> item,
        string condition,
        Dictionary<string, AttributeValue>? values,
        CancellationToken deadline) =>
        table.Client.PutItemAsync(
            new PutItemRequest
            {
                TableName = table.TableName,
                Item = Copy(item),
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
            },
            deadline);

    /// <summary>The item for a record under <paramref name="pk"/>, its ticket freshly protected.</summary>
    private Dictionary<string, AttributeValue> Item(string pk, SessionRecord record) => new()
    {
        ["pk"] = new() { S = pk },
        ["appId"] = new() { S = record.AppId },
        ["clientId"] = new() { S = record.ClientId },
        ["subject"] = new() { S = record.Subject },
        ["createdAt"] = new() { S = record.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
        ["recordExpiresAt"] = new() { S = record.ExpiresAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
        ["expiresAt"] = new() { N = record.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
        ["version"] = new() { N = record.Version.ToString(CultureInfo.InvariantCulture) },
        ["ticket"] = new() { B = new MemoryStream(Protector(record.AppId).Protect(record.Ticket.ToArray())) },
        ["format"] = new() { N = Format.ToString(CultureInfo.InvariantCulture) },
    };

    /// <summary>
    /// A fresh copy of an item for each send: the SDK reads a binary attribute's stream, so two sends
    /// never share one, and the item kept for comparison is never touched.
    /// </summary>
    private static Dictionary<string, AttributeValue> Copy(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.B is { } bytes ? new AttributeValue { B = new MemoryStream(bytes.ToArray()) } : new AttributeValue { S = pair.Value.S, N = pair.Value.N },
            StringComparer.Ordinal);

    private IDataProtector Protector(string appId) => table.Protection.CreateProtector(Purpose, appId);

    private long NowSeconds() => table.Clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>
    /// The record an item holds, or null: no item, an expired one, one in a format this build does not
    /// read, or one whose ticket this gateway's key ring cannot unprotect.
    /// </summary>
    private SessionRecord? Live(Dictionary<string, AttributeValue>? item)
    {
        if (item is not { Count: > 0 })
        {
            return null;
        }

        // MR-9: a rolled-back image reads an item a later image wrote as absent and says so once, so the
        // user signs in again rather than meeting a 500 on every request. Format 1 is the first, so there
        // is no "one before it" yet; the build that writes format 2 reads 1 as well.
        string? format = item.TryGetValue("format", out AttributeValue? f) ? f.N : null;
        if (format != "1")
        {
            if (Interlocked.Exchange(ref formatLogged, 1) == 0)
            {
                table.Logger.LogWarning(
                    new EventId(1, "SessionFormatUnknown"),
                    "A session item carries format {Format}, which this build does not read; such sessions read as absent and their users sign in again.",
                    format ?? "(none)");
            }

            return null;
        }

        if (long.Parse(item["expiresAt"].N, CultureInfo.InvariantCulture) <= NowSeconds())
        {
            return null;
        }

        string appId = item["appId"].S;
        byte[] ticket;
        try
        {
            ticket = Protector(appId).Unprotect(item["ticket"].B.ToArray());
        }
        catch (CryptographicException)
        {
            // A ticket protected under a key ring this task does not share (P2-2 not yet in place, or a
            // ring that was replaced). Absent, as an unknown format is, rather than a 500 on every request
            // that presents the cookie; said once, and naming no key.
            if (Interlocked.Exchange(ref unprotectLogged, 1) == 0)
            {
                table.Logger.LogWarning(
                    new EventId(2, "SessionTicketUnreadable"),
                    "A session ticket in the table could not be unprotected with this gateway's key ring; such sessions read as absent.");
            }

            return null;
        }

        return new SessionRecord(
            AppId: appId,
            ClientId: item["clientId"].S,
            Subject: item["subject"].S,
            CreatedAt: DateTimeOffset.ParseExact(item["createdAt"].S, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAt: DateTimeOffset.ParseExact(item["recordExpiresAt"].S, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Version: long.Parse(item["version"].N, CultureInfo.InvariantCulture),
            Ticket: ticket);
    }
}
