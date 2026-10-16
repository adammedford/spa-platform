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
    [InlineData("dynamodb", "10")]
    [InlineData(null, "10")]
    [InlineData("memory", null)]
    [InlineData("memory", "0")]
    [InlineData("memory", "many")]
    public void A_session_setting_the_memory_store_cannot_honour_is_refused(string? kind, string? max)
    {
        // Phase 2's store is not built yet; a missing or unusable bound is not guessed at.
        using IHost host = Build(new() { ["Session:Store"] = kind, ["Session:MaxSessions"] = max });

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ISessionStore>());
        Assert.Contains("Session:", refused.Message, StringComparison.Ordinal);
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
