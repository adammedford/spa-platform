using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Platform.Abstractions;
using static Platform.Identity.DynamoDbSessionTable;

namespace Platform.Identity;

/// <summary>
/// The refresh lease on DynamoDB (ADR-0024; change request P2-1, Decision 7; #332 D4 and F12). It
/// passes <c>Platform.Testing.RefreshLeasesContract</c>.
///
/// <para><b>Its own item.</b> <c>pk</c> is <see cref="DynamoDbSessionTable.LeasePrefix"/> and the
/// session key's hash; <c>leaseToken</c>; <c>leaseUntil</c> in epoch milliseconds, because a 21 s lease
/// compared in seconds loses up to a second; <c>expiresAt</c>, the lease's expiry in seconds rounded
/// up, so the table's time to live sweeps a lease whose holder died and never one still held. No
/// <c>appId</c>, so the <c>appId</c> index never projects it and a purge never sees it. On its own item
/// the lease neither needs a session record (ADR-0024 decision 3) nor is erased by the session's
/// <c>PutItem</c>.</para>
///
/// <para><b>Acquire</b> is a <c>PutItem</c> under
/// <c>attribute_not_exists(pk) OR leaseUntil &lt; :now OR leaseToken = :token</c>: free, lapsed, or
/// already ours. The last clause is D4's: when the first send's outcome is unknown, the one retry sends
/// the very same item, token and <c>leaseUntil</c> unchanged, so a first send that had committed is
/// re-established, never renewed. The retry is this adapter's own, inside one
/// <see cref="TryAcquireAsync"/>, because <see cref="IRefreshLeases"/> takes no token (G3 on #332).
/// A success that arrives after the lease's own expiry confers nothing and reads as null: the caller
/// becomes a loser and re-reads, as it would had another holder won.</para>
///
/// <para><b>Release</b> is a <c>DeleteItem</c> under <c>leaseToken = :token</c>. A failed condition is
/// the stale release P2-3's Decision item 5 swallows: the lease expired and another holder took it, or
/// it was released already. One retry on an unknown outcome, since the fenced delete is safe to repeat;
/// still unknown, the release throws, and the lease lapses at its expiry in any case.</para>
/// </summary>
internal sealed class DynamoDbRefreshLeases(DynamoDbSessionTable table) : IRefreshLeases
{
    private const string Acquire = "lease acquire (PutItem)";
    private const string Release = "lease release (DeleteItem)";

    public async Task<RefreshLease?> TryAcquireAsync(string sessionKey, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        long now = table.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        long leaseUntil = now + (long)ttl.TotalMilliseconds;
        string token = Guid.NewGuid().ToString("n");
        RefreshLease lease = new(token, DateTimeOffset.FromUnixTimeMilliseconds(leaseUntil));

        PutItemRequest Request() => new()
        {
            TableName = table.TableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = LeasePk(sessionKey) },
                ["leaseToken"] = new() { S = token },
                ["leaseUntil"] = new() { N = leaseUntil.ToString(CultureInfo.InvariantCulture) },
                ["expiresAt"] = new() { N = ((leaseUntil + 999) / 1000).ToString(CultureInfo.InvariantCulture) },
            },
            ConditionExpression = "attribute_not_exists(pk) OR leaseUntil < :now OR leaseToken = :token",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":now"] = new() { N = now.ToString(CultureInfo.InvariantCulture) },
                [":token"] = new() { S = token },
            },
        };

        using CancellationTokenSource deadline = table.Deadline();
        Attempt<PutItemResponse> sent = await TryAsync(d => table.Client.PutItemAsync(Request(), d), deadline.Token);
        if (sent.Outcome == Outcome.Unknown)
        {
            sent = await TryAsync(d => table.Client.PutItemAsync(Request(), d), deadline.Token);
        }

        return sent.Outcome switch
        {
            // Held up to and including its expiry, as RefreshLeasesContract reads it; after that, nothing.
            Outcome.Done => table.Clock.GetUtcNow() <= lease.ExpiresAt ? lease : null,
            Outcome.ConditionFailed => null,
            _ => throw new SessionStoreUnavailableException(Acquire, table.StoreDeadline, sent.Error),
        };
    }

    public async Task ReleaseAsync(string sessionKey, RefreshLease lease, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        DeleteItemRequest Request() => new()
        {
            TableName = table.TableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = LeasePk(sessionKey) } },
            ConditionExpression = "leaseToken = :token",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":token"] = new() { S = lease.Token } },
        };

        using CancellationTokenSource deadline = table.Deadline();
        Attempt<DeleteItemResponse> sent = await TryAsync(d => table.Client.DeleteItemAsync(Request(), d), deadline.Token);
        if (sent.Outcome == Outcome.Unknown)
        {
            sent = await TryAsync(d => table.Client.DeleteItemAsync(Request(), d), deadline.Token);
        }

        if (sent.Outcome == Outcome.Unknown)
        {
            throw new SessionStoreUnavailableException(Release, table.StoreDeadline, sent.Error);
        }
    }

    private static string LeasePk(string sessionKey) => LeasePrefix + HashOf(sessionKey);
}
