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
    /// The refresh lease over the same table, when the store is <c>dynamodb</c>, and none otherwise. The
    /// memory store runs on one replica, where the in-process session gate of <see cref="AppSessions"/> is
    /// the whole refresh gate and there is no other task to lease against. An unset <c>Session:Store</c>
    /// is a host that brought its own store (the component tests' <c>IdentityGateway</c>), which brings its
    /// own lease too when it runs more than one replica. Null there, not a refusal, so a service that takes
    /// the lease as an optional dependency, as the token source of P2-3 does, is built without one (#393;
    /// a throw here failed #341's merge, #384). A value that names neither store is refused as
    /// <see cref="FromConfiguration"/> refuses it.
    /// </summary>
    public static IRefreshLeases? LeasesFromConfiguration(IServiceProvider services) => Kind(services) switch
    {
        "dynamodb" => new DynamoDbRefreshLeases(services.GetRequiredService<DynamoDbSessionTable>()),
        "memory" or null => null,
        var other => throw Unknown(other),
    };

    private static string? Kind(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>()["Session:Store"];

    private static InvalidOperationException Unknown(string? kind) =>
        new($"Session:Store is '{kind}'. It must be 'memory' or 'dynamodb'.");
}
