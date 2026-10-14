using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Hosting.Tests;

/// <summary>
/// Mapping the asset endpoint builds no object store. The gateway's is the S3 store, whose client
/// throws with no region or bucket configured, and <c>RegistryPoller</c> defers it so the gateway
/// starts with no <c>Registry:*</c> at all (CI's G1a smoke test); stage 10 must not undo that.
/// </summary>
public sealed class AssetStartupTests
{
    [Fact]
    public async Task Mapping_the_asset_endpoint_does_not_build_the_object_store()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int built = 0;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRegistry>(new Platform.Testing.InMemoryRegistry(Snapshots.None));
        builder.Services.AddSingleton<IObjectStore>(_ =>
        {
            Interlocked.Increment(ref built);
            throw new InvalidOperationException("No region configured.");
        });
        builder.AddPlatformHosting();

        await using WebApplication web = builder.Build();
        web.UsePlatformHealth();
        web.UsePlatformHostResolution();
        web.UseRouting();
        web.MapPlatformAssets();

        await web.StartAsync(ct);
        await web.StopAsync(ct);

        Assert.Equal(0, Volatile.Read(ref built));
    }
}
