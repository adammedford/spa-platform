using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Testing.Internal;

/// <summary>
/// The loopback start shared by <see cref="StageHost"/> and <see cref="EchoUpstreamListener"/>: its
/// two callers, and no public name (change request issue-145, section 7). The first half is steps 1
/// to 3 of the stage host's construction order, the second is step 10.
/// </summary>
internal static class LoopbackHost
{
    /// <summary>
    /// The slim builder in the <c>Production</c> environment, as the test identity provider and the
    /// gateway test harness set it; Kestrel on a loopback port the operating system picks; and every
    /// logger provider cleared, so nothing a caller does not add writes anywhere.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    /// <summary>
    /// Starts <paramref name="app"/> and reads the address Kestrel bound. A start that yields no
    /// address throws, because every caller's next step is to send to it.
    /// </summary>
    public static async Task<Uri> StartAsync(WebApplication app, string what, CancellationToken ct)
    {
        await app.StartAsync(ct);

        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"{what} started without an address.");

        return new Uri(address);
    }
}
