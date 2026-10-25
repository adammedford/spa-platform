using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The store <c>Session:Store</c> names (contracts/platform-config.schema.json): <c>memory</c>, the
/// Phase 1 store on one replica (plan row 3.4), or <c>dynamodb</c>, the session table every task shares
/// (plan row P2-1). Any other value, or none, is refused rather than guessed at. Each is built on first
/// use, so a gateway that never touches a session (the ALB health check, tools/smoke.sh) needs no
/// session settings and makes no call to the table.
/// </summary>
internal static class SessionStores
{
    public static ISessionStore FromConfiguration(IServiceProvider services) => Kind(services) switch
    {
        "memory" => MemorySessionStore.FromConfiguration(services),
        "dynamodb" => new DynamoDbSessionStore(services.GetRequiredService<DynamoDbSessionTable>()),
        var other => throw Unknown(other),
    };

    /// <summary>
    /// The refresh lease over the same table, when the store is <c>dynamodb</c>. The memory store runs on
    /// one replica, where the in-process session gate of <see cref="AppSessions"/> is the whole refresh
    /// gate and there is no other task to lease against, so it has none, and asking for one is refused
    /// with that reason rather than handed a lease that guards nothing.
    /// </summary>
    public static IRefreshLeases LeasesFromConfiguration(IServiceProvider services) => Kind(services) switch
    {
        "dynamodb" => new DynamoDbRefreshLeases(services.GetRequiredService<DynamoDbSessionTable>()),
        "memory" => throw new InvalidOperationException(
            "Session:Store is 'memory', which runs on one replica and has no refresh lease: the session gate is the whole refresh gate there."),
        var other => throw Unknown(other),
    };

    private static string? Kind(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>()["Session:Store"];

    private static InvalidOperationException Unknown(string? kind) =>
        new($"Session:Store is '{kind}'. It must be 'memory' or 'dynamodb'.");
}
