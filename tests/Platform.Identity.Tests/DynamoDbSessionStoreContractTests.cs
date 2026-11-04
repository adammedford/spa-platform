using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Plan row P2-1: "the store contract suite passes". The DynamoDB store passes the suite the memory store
/// passes, over <see cref="FakeDynamoDbServer"/>, so every record it hands back has been serialised to the
/// table's JSON, sent over HTTP, and read back on a consistent read.
/// </summary>
public sealed class DynamoDbSessionStoreContractTests : SessionStoreContract, IAsyncLifetime
{
    private FakeDynamoDbServer fake = null!;

    public async ValueTask InitializeAsync() => fake = await FakeDynamoDbServer.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await fake.DisposeAsync();

    // The suite's records expire at 17:00 on the shared clock's day, so a store reading the shared clock
    // sees every one of them as live.
    protected override ISessionStore CreateStore() => new DynamoDbSessionStore(DynamoDbTables.Over(fake, TestClock.New()));
}

/// <summary>
/// Change request P2-1, Decision 7: the refresh lease on its own item passes the suite the in-memory
/// double passes, over the same fake, with the suite's clock as the store's.
/// </summary>
public sealed class DynamoDbRefreshLeasesContractTests : RefreshLeasesContract, IAsyncLifetime
{
    private FakeDynamoDbServer fake = null!;

    public async ValueTask InitializeAsync() => fake = await FakeDynamoDbServer.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await fake.DisposeAsync();

    protected override IRefreshLeases CreateLeases(TimeProvider clock) => new DynamoDbRefreshLeases(DynamoDbTables.Over(fake, clock));
}

/// <summary>The session table over a fake, as the tests build it.</summary>
internal static class DynamoDbTables
{
    /// <summary>
    /// A table over <paramref name="fake"/> with the SDK's retries off, reading <paramref name="clock"/>
    /// for expiry and for <c>StoreDeadline</c>, with an ephemeral key ring unless one is given.
    /// </summary>
    public static DynamoDbSessionTable Over(
        FakeDynamoDbServer fake,
        TimeProvider clock,
        ILogger? logger = null,
        TimeSpan? storeDeadline = null,
        IDataProtectionProvider? protection = null,
        int? maxErrorRetry = 0) =>
        new(
            fake.Client(maxErrorRetry),
            FakeDynamoDbServer.Table,
            protection ?? new EphemeralDataProtectionProvider(),
            clock,
            logger ?? NullLogger.Instance,
            storeDeadline);
}
