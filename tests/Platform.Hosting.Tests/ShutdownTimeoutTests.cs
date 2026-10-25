using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Hosting.Tests;

/// <summary>
/// <c>AddPlatformHosting</c> sets the host's <c>HostOptions.ShutdownTimeout</c> to 25 seconds, a
/// constant in <c>Platform.Hosting</c> and not configuration (#363; #332 F1). The expected value is
/// the spec's literal, not the constant read back.
///
/// What the timeout does on a stop, finishing a request in flight that is shorter and aborting one
/// that is longer, is the host's own behaviour; proving it at the real value costs 25 seconds a run,
/// so these tests pin the value and that the entry point applies it.
/// </summary>
public sealed class ShutdownTimeoutTests
{
    [Fact]
    public async Task AddPlatformHosting_sets_the_shutdown_timeout_to_25_seconds()
    {
        await using WebApplication web = Build(_ => { });

        Assert.Equal(TimeSpan.FromSeconds(25), web.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), PlatformHostingLimits.ShutdownTimeout);
    }

    [Fact]
    public async Task The_host_configuration_does_not_move_the_shutdown_timeout()
    {
        // HostOptions reads shutdownTimeoutSeconds from the host's configuration on its own; the
        // timeout is a constant, so that key must not move it.
        await using WebApplication web = Build(builder => builder.Configuration["shutdownTimeoutSeconds"] = "90");

        Assert.Equal(TimeSpan.FromSeconds(25), web.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task A_Configure_registered_after_AddPlatformHosting_does_not_move_the_shutdown_timeout()
    {
        // The reason for PostConfigure over Configure: a plain Configure in AddPlatformHosting would
        // still beat the host's own binding, registered earlier, but lose to this later one (#363 review).
        await using WebApplication web = Build(
            _ => { },
            builder => builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(90)));

        Assert.Equal(TimeSpan.FromSeconds(25), web.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    private static WebApplication Build(Action<WebApplicationBuilder> configure, Action<WebApplicationBuilder>? after = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRegistry>(new Platform.Testing.InMemoryRegistry(Snapshots.None));
        configure(builder);
        builder.AddPlatformHosting();
        after?.Invoke(builder);
        return builder.Build();
    }
}
