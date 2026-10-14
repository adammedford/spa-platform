using System.Text;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Registry.Tests;

/// <summary>
/// The poll loop around <see cref="RegistrySource"/>: <c>Registry:PollSeconds</c> jittered by up to
/// 20% (docs/plan/02-contracts.md, Platform configuration), started by <c>AddPlatformRegistry</c>, and
/// never able to stop the host. CI's G1a smoke test starts the composed gateway with no platform
/// configuration and no AWS region, so that case is proved here too.
/// </summary>
public sealed class RegistryPollerTests
{
    private const string Key = "registry/registry.json";
    private const string App1Ref = "ssm:/spa-platform/poc/apps/app1/client-secret:1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.999999)]
    public void The_delay_is_the_interval_less_up_to_a_fifth(double sample)
    {
        // Decided by implementer: jitter only shortens the wait, so a registry change is live within one
        // poll interval as s3-layout.md promises ("Activation is a registry change").
        TimeSpan interval = TimeSpan.FromSeconds(15);

        TimeSpan delay = RegistryPoller.NextDelay(interval, sample);

        Assert.InRange(delay, TimeSpan.FromSeconds(12), interval);
    }

    [Fact]
    public void The_delay_varies_with_the_sample()
    {
        TimeSpan interval = TimeSpan.FromSeconds(15);

        Assert.Equal(interval, RegistryPoller.NextDelay(interval, 0.0));
        Assert.Equal(TimeSpan.FromSeconds(13.5), RegistryPoller.NextDelay(interval, 0.5));
    }

    [Fact]
    public async Task With_no_registry_configuration_the_host_starts_and_nothing_goes_live()
    {
        int clients = 0;
        ListLoggerProvider logs = new();
        HostApplicationBuilder builder = Builder(logs, configured: false);
        // The SDK client's constructor throws with no region configured (seen on SDK 4.0.101.6), so the
        // poller must not build one when there is nothing to poll.
        builder.Services.AddSingleton<IAmazonS3>(_ =>
        {
            Interlocked.Increment(ref clients);
            throw new InvalidOperationException("no region");
        });
        builder.AddPlatformRegistry();

        using IHost host = builder.Build();
        await host.StartAsync(Ct);
        await Eventually(() => logs.Lines.Any(line => line.Contains("Registry:", StringComparison.Ordinal)));
        await host.StopAsync(Ct);

        Assert.Equal(0, host.Services.GetRequiredService<IRegistry>().Current.Revision);
        Assert.Equal(0, clients);
    }

    [Fact]
    public async Task The_poller_reads_at_start_and_again_after_one_interval()
    {
        FakeTimeProvider clock = TestClock.New();
        RecordingObjectStore store = await StoreWith(Examples.Text("registry.example.json"));
        HostApplicationBuilder builder = Builder(new ListLoggerProvider(), configured: true);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IObjectStore>(store);
        builder.Services.AddSingleton<IClientSecretSource>(new FakeClientSecretSource { [App1Ref] = "s3cret" });
        builder.AddPlatformRegistry();

        using IHost host = builder.Build();
        IRegistry registry = host.Services.GetRequiredService<IRegistry>();
        await host.StartAsync(Ct);
        await Eventually(() => registry.Current.Revision == 42);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        Assert.Single(store.Reads);

        await Eventually(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            return store.Reads.Count >= 2;
        });
        await host.StopAsync(Ct);

        Assert.Equal(Key, store.Reads[1].Key);
        Assert.True(store.Reads[1].NotModified);
    }

    [Fact]
    public async Task A_poll_that_throws_does_not_stop_the_host_and_the_next_one_runs()
    {
        // BackgroundService's default is to stop the host when ExecuteAsync throws
        // (https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/hosting-exception-handling).
        FakeTimeProvider clock = TestClock.New();
        RecordingObjectStore store = await StoreWith(Examples.Text("registry.example.json"));
        int attempts = 0;
        HostApplicationBuilder builder = Builder(new ListLoggerProvider(), configured: true);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IObjectStore>(_ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("no region") : store);
        builder.Services.AddSingleton<IClientSecretSource>(new FakeClientSecretSource { [App1Ref] = "s3cret" });
        builder.AddPlatformRegistry();

        using IHost host = builder.Build();
        IRegistry registry = host.Services.GetRequiredService<IRegistry>();
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await host.StartAsync(Ct);
        await Eventually(() => attempts >= 1);

        await Eventually(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            return registry.Current.Revision == 42;
        });

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(Ct);
    }

    private static HostApplicationBuilder Builder(ListLoggerProvider logs, bool configured)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging(logging => logging.AddProvider(logs));
        if (configured)
        {
            // The contract's own example configuration: Registry:*, Policy:* and Issuers:*.
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "examples", "platform-config.example.json"));
        }

        return builder;
    }

    private static async Task<RecordingObjectStore> StoreWith(string document)
    {
        InMemoryObjectStore inner = new();
        using MemoryStream body = new(Encoding.UTF8.GetBytes(document));
        await inner.PutAsync(Key, body, null, Ct);
        return new RecordingObjectStore(inner);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the condition did not hold within 10 seconds");
            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }
}
