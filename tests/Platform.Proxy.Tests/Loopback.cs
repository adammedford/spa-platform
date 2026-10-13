using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy.Tests;

/// <summary>
/// The two bits every host in these tests needs: a loopback port the operating system picks, and the
/// address it picked. <c>Platform.Testing.EchoUpstreamListener</c> does the same thing, and a shared
/// helper for all three belongs there, which is an integrator path.
/// </summary>
internal static class Loopback
{
    /// <summary>A builder on a loopback port, with the console logger out of the way.</summary>
    internal static WebApplicationBuilder Builder()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        return builder;
    }

    /// <summary>Where a started application is listening.</summary>
    internal static Uri AddressOf(WebApplication app, string what) =>
        new(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"the {what} started without an address."));
}
