using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>AddPlatformIdentity</c> supplies the gateway's <see cref="ISessionStore"/> from
/// <c>Session:Store</c> and <c>Session:MaxSessions</c> (docs/plan/02-contracts.md -> Platform
/// configuration, contracts/platform-config.schema.json).
/// </summary>
public sealed class SessionStoreRegistrationTests
{
    [Fact]
    public async Task The_memory_store_is_bound_by_Session_MaxSessions()
    {
        using IHost host = Build(
            new() { ["Session:Store"] = "memory", ["Session:MaxSessions"] = "1" },
            services => services.AddSingleton<TimeProvider>(TestClock.New()));
        ISessionStore store = host.Services.GetRequiredService<ISessionStore>();

        string first = await store.CreateAsync(Record(TimeSpan.FromHours(2)), Ct);
        string second = await store.CreateAsync(Record(TimeSpan.FromHours(8)), Ct);

        Assert.Null(await store.GetAsync(first, Ct));
        Assert.NotNull(await store.GetAsync(second, Ct));
    }

    [Fact]
    public async Task The_memory_store_reads_the_registered_clock()
    {
        FakeTimeProvider clock = TestClock.New();
        using IHost host = Build(
            new() { ["Session:Store"] = "memory", ["Session:MaxSessions"] = "10" },
            services => services.AddSingleton<TimeProvider>(clock));
        ISessionStore store = host.Services.GetRequiredService<ISessionStore>();
        string key = await store.CreateAsync(Record(TimeSpan.FromHours(8)), Ct);

        clock.Advance(TimeSpan.FromHours(8));

        Assert.Null(await store.GetAsync(key, Ct));
    }

    [Fact]
    public void A_store_registered_beforehand_is_kept()
    {
        // The component tests, and any host that brings its own store, register it first.
        InMemorySessionStore own = new();
        using IHost host = Build([], services => services.AddSingleton<ISessionStore>(own), before: true);

        Assert.Same(own, host.Services.GetRequiredService<ISessionStore>());
    }

    [Theory]
    [InlineData(null, "10")]
    [InlineData("redis", "10")]
    [InlineData("memory", null)]
    [InlineData("memory", "0")]
    [InlineData("memory", "many")]
    public void A_session_setting_the_memory_store_cannot_honour_is_refused(string? kind, string? max)
    {
        // A store nobody named, or a missing or unusable bound, is not guessed at.
        using IHost host = Build(new() { ["Session:Store"] = kind, ["Session:MaxSessions"] = max });

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ISessionStore>());
        Assert.Contains("Session:", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void The_dynamodb_store_without_a_table_name_is_refused_as_the_memory_store_refuses_a_missing_bound(string? table)
    {
        // Change request P2-1, Decision 3: the store refuses a missing table name whatever the schema says
        // of it. Mutation: default the table's name, and the store is built.
        using IHost host = Build(
            new() { ["Session:Store"] = "dynamodb", ["Session:TableName"] = table },
            services => services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient(new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://127.0.0.1:9/" })));

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ISessionStore>());
        Assert.Contains("Session:TableName", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IRefreshLeases>());
    }

    [Fact]
    public async Task Session_Store_dynamodb_builds_the_store_and_the_lease_over_the_client_the_host_registered()
    {
        // The client comes from the container, so a host that registers its own wins over the default
        // (TryAddSingleton). Mutation: construct the client inside the store, and the fake sees nothing.
        await using FakeDynamoDbServer fake = await FakeDynamoDbServer.StartAsync(Ct);
        using IHost host = Build(
            new() { ["Session:Store"] = "dynamodb", ["Session:TableName"] = FakeDynamoDbServer.Table },
            services => services.AddSingleton<IAmazonDynamoDB>(fake.Client()).AddSingleton<TimeProvider>(TestClock.New()));

        ISessionStore store = host.Services.GetRequiredService<ISessionStore>();
        IRefreshLeases leases = host.Services.GetRequiredService<IRefreshLeases>();
        string key = await store.CreateAsync(Record(TimeSpan.FromHours(8)), Ct);

        Assert.IsType<DynamoDbSessionStore>(store);
        Assert.IsType<DynamoDbRefreshLeases>(leases);
        Assert.NotNull(await store.GetAsync(key, Ct));
        Assert.NotNull(await leases.TryAcquireAsync(key, TimeSpan.FromSeconds(21), Ct));
        Assert.Equal(["PutItem", "GetItem", "PutItem"], fake.Operations);
    }

    [Fact]
    public void The_memory_store_has_no_refresh_lease_and_says_why()
    {
        // One replica: the session gate is the whole refresh gate. Mutation: hand back a DynamoDB lease for
        // the memory store, and its construction asks for a table nobody configured.
        using IHost host = Build(new() { ["Session:Store"] = "memory", ["Session:MaxSessions"] = "10" });

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IRefreshLeases>());
        Assert.Contains("one replica", refused.Message, StringComparison.Ordinal);
    }

    private static IHost Build(
        Dictionary<string, string?> settings, Action<IServiceCollection>? services = null, bool before = false)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        if (before)
        {
            services?.Invoke(builder.Services);
        }

        builder.AddPlatformIdentity();
        if (!before)
        {
            services?.Invoke(builder.Services);
        }

        return builder.Build();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionRecord Record(TimeSpan expiresIn) => new(
        AppId: "app1",
        ClientId: "1example23456789",
        Subject: "user-1",
        CreatedAt: TestClock.Instant,
        ExpiresAt: TestClock.Instant + expiresIn,
        Version: 1,
        Ticket: new byte[] { 7, 7, 7 });
}
