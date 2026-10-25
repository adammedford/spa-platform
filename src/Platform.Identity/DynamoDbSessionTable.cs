using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Identity;

/// <summary>
/// The session table both DynamoDB adapters write to: <see cref="DynamoDbSessionStore"/> and
/// <see cref="DynamoDbRefreshLeases"/> (plan row P2-1; change request P2-1, Decision 1 to 9; the
/// <c>continuity</c> spec, #332, D4). It holds what the two share: the client, the table's name, the
/// clock, the key hash and <see cref="StoreDeadline"/>, and the one way a call to the table is made.
///
/// <para><b>One call.</b> Every request to the table goes through <see cref="TryAsync{T}"/>, under the
/// operation's deadline and never under the caller's token (D4, T-B17). The SDK, cancelled after it
/// has sent a request, throws <see cref="TaskCanceledException"/> and leaves no sign of whether the
/// write committed: the request has reached the table by then, and the SDK makes no further attempt
/// (shown by <c>DynamoDbProtocolTests.A_cancellation_after_send_reaches_the_table_and_the_SDK_reports_only_that_it_was_cancelled</c>
/// at the pin). A caller's cancellation would therefore turn a write that happened into one whose
/// outcome is unknown. So the caller's token is read once, before anything is sent, and the only
/// cancellation that reaches the SDK is the deadline's.</para>
///
/// <para><b>Three outcomes.</b> A call is <see cref="Outcome.Done"/>, <see cref="Outcome.ConditionFailed"/>
/// (the 400 <c>ConditionalCheckFailedException</c>, which DynamoDB answers before applying anything:
/// https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_PutItem.html#API_PutItem_Errors),
/// or <see cref="Outcome.Unknown"/>: anything else, a 5xx, a broken connection, a refused one, the
/// deadline. Unknown is not "failed": a 500 or a dropped response can follow a commit, so each
/// adapter decides what an unknown write means by re-reading or by retrying a write that is safe to
/// repeat, as D4 sets out per operation.</para>
///
/// <para><b>The SDK's own retries.</b> A client from the container keeps its own retry policy, which
/// for DynamoDB is 10 retries by default (11 attempts were seen at the pin). They run inside the
/// deadline, so <see cref="StoreDeadline"/> still bounds the operation, and each adapter reads a
/// condition failure as possibly its own earlier attempt, since an SDK retry of a committed write
/// fails its own condition.</para>
/// </summary>
internal sealed class DynamoDbSessionTable
{
    /// <summary>
    /// D4: 5 s bounds each operation, its re-read and its one retry together. The lease's margin is
    /// <c>2 × StoreDeadline + 1 s</c> (change request P2-3, amended 2026-10-24), so this figure is read
    /// there as well.
    /// </summary>
    internal static readonly TimeSpan DefaultStoreDeadline = TimeSpan.FromSeconds(5);

    /// <summary>The <c>appId</c> index, keys only (infra/modules/cell/iam.tf, <c>session_table_index_name</c>).</summary>
    internal const string AppIndex = "appId";

    /// <summary>What a lease item's <c>pk</c> starts with. No 64-character hex session <c>pk</c> can (P2-1 Decision 7).</summary>
    internal const string LeasePrefix = "lease#";

    public DynamoDbSessionTable(
        IAmazonDynamoDB client,
        string tableName,
        IDataProtectionProvider protection,
        TimeProvider clock,
        ILogger logger,
        TimeSpan? storeDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        Client = client;
        TableName = tableName;
        Protection = protection;
        Clock = clock;
        Logger = logger;
        StoreDeadline = storeDeadline ?? DefaultStoreDeadline;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StoreDeadline, TimeSpan.Zero);
    }

    public IAmazonDynamoDB Client { get; }

    public string TableName { get; }

    public IDataProtectionProvider Protection { get; }

    public TimeProvider Clock { get; }

    public ILogger Logger { get; }

    public TimeSpan StoreDeadline { get; }

    /// <summary>
    /// The table <c>Session:TableName</c> names, with the client from the container. Refused without a
    /// name, as the memory store refuses a missing bound: the schema's <c>Store = dynamodb</c> requires
    /// <c>TableName</c> only from the next gate (change request P2-1, Decision 3), so until then this is
    /// the one place the rule lives.
    /// </summary>
    internal static DynamoDbSessionTable FromConfiguration(IServiceProvider services)
    {
        IConfiguration configuration = services.GetRequiredService<IConfiguration>();
        string? tableName = configuration["Session:TableName"];
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new InvalidOperationException(
                "Session:Store is 'dynamodb' and Session:TableName is not set. The DynamoDB store needs its table's name.");
        }

        return new DynamoDbSessionTable(
            services.GetRequiredService<IAmazonDynamoDB>(),
            tableName,
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<DynamoDbSessionStore>());
    }

    /// <summary>
    /// The lowercase hex SHA-256 of the session key's UTF-8 bytes: the session item's <c>pk</c> (P2-1
    /// item 6). Its first twelve characters are <c>SessionRef.Of(sessionKey)</c> by construction, so a
    /// supporter can match a log line to a row, and the table never holds the key, which is a
    /// credential (contracts/telemetry.md).
    /// </summary>
    internal static string HashOf(string sessionKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)));

    /// <summary>A deadline of <see cref="StoreDeadline"/> on the store's clock, for one operation.</summary>
    public CancellationTokenSource Deadline() => new(StoreDeadline, Clock);

    /// <summary>
    /// One call to the table under <paramref name="deadline"/>. Never throws for what the table or the
    /// network did; the caller reads the outcome. A deadline that has already passed is unknown at once.
    /// </summary>
    public static async Task<Attempt<T>> TryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken deadline)
    {
        if (deadline.IsCancellationRequested)
        {
            return new Attempt<T>(Outcome.Unknown, default, new OperationCanceledException(deadline));
        }

        try
        {
            return new Attempt<T>(Outcome.Done, await call(deadline), null);
        }
        catch (ConditionalCheckFailedException)
        {
            return new Attempt<T>(Outcome.ConditionFailed, default, null);
        }
        catch (Exception unknown) when (unknown is not ArgumentException)
        {
            // An argument the SDK refuses is this code's fault, not the table's, and stays loud.
            return new Attempt<T>(Outcome.Unknown, default, unknown);
        }
    }

    /// <summary>A consistent read by <c>pk</c> (P2-1 item 5: a default read is eventually consistent).</summary>
    public Task<GetItemResponse> GetAsync(string pk, CancellationToken deadline) =>
        Client.GetItemAsync(
            new GetItemRequest
            {
                TableName = TableName,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = pk } },
                ConsistentRead = true,
            },
            deadline);

    /// <summary>
    /// Whether two items hold the same attributes with the same values: how an adapter recognises its own
    /// write after an unknown outcome. The ticket's ciphertext is fresh for every protect, so two
    /// different writes of one record never compare equal.
    /// </summary>
    public static bool SameItem(Dictionary<string, AttributeValue>? stored, Dictionary<string, AttributeValue> written)
    {
        if (stored is null || stored.Count != written.Count)
        {
            return false;
        }

        foreach ((string name, AttributeValue value) in written)
        {
            if (!stored.TryGetValue(name, out AttributeValue? other)
                || !string.Equals(value.S, other.S, StringComparison.Ordinal)
                || !string.Equals(value.N, other.N, StringComparison.Ordinal)
                || !BytesEqual(value.B, other.B))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BytesEqual(MemoryStream? a, MemoryStream? b) =>
        a is null ? b is null : b is not null && a.ToArray().AsSpan().SequenceEqual(b.ToArray());

    internal enum Outcome
    {
        Done,
        ConditionFailed,
        Unknown,
    }

    internal readonly record struct Attempt<T>(Outcome Outcome, T? Value, Exception? Error);
}

/// <summary>
/// A session-store operation that did not complete: it failed, or its outcome was still unknown when
/// <see cref="DynamoDbSessionTable.StoreDeadline"/> ran out (D4). It names the operation and nothing
/// else: no key, no <c>pk</c>, no record (N-16). Nothing in the identity chain catches it, so the request
/// ends on the gateway's generic 500, where the server writes the one Error line with this message,
/// and no cookie is set or cleared on the way (F17; a <c>503 session_store_unavailable</c> row waits for
/// the next gate).
/// </summary>
internal sealed class SessionStoreUnavailableException(string operation, TimeSpan deadline, Exception? inner)
    : Exception(
        $"The session store's {operation} did not complete: it failed, or its outcome was unknown when StoreDeadline "
        + $"({deadline.TotalSeconds:0.###} s) ran out.",
        inner)
{
    /// <summary>The operation, as the message names it: <c>create (PutItem)</c>, <c>read (GetItem)</c> and so on.</summary>
    public string Operation { get; } = operation;
}
