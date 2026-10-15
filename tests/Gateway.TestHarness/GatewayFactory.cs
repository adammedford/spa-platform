extern alias GatewayApp;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.TestHarness;

/// <summary>
/// The gateway's own composition root (<c>src/Gateway/Program.cs</c>), started on Kestrel on a loopback
/// port with configuration and service replacements laid over it. Nothing in the pipeline is replaced:
/// every stage runs in its frozen order, so what the composed suites prove is the gateway that ships.
///
/// Kestrel and not the in-memory test server, for three reasons the suites depend on: Kestrel's own
/// request limits answer 431 (N-20), the client address comes from a real connection so the forwarded
/// headers stage has a sender to judge (N-09), and a header only becomes wrong once it has been
/// serialized onto a wire.
/// </summary>
internal sealed class GatewayFactory : WebApplicationFactory<GatewayApp::Program>
{
    private readonly IReadOnlyDictionary<string, string?> settings;
    private readonly Action<IServiceCollection> services;

    public GatewayFactory(IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection> services)
    {
        this.settings = settings;
        this.services = services;

        // A real listener on a loopback port the system picks.
        UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
    }

    /// <summary>Where the gateway listens, e.g. <c>http://127.0.0.1:54321/</c>. Known once started.</summary>
    public Uri Address
    {
        get
        {
            string address = Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("the gateway started without an address.");
            return new Uri(address.EndsWith('/') ? address : address + "/");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production, as the cell runs it. Development would switch on behaviour the gateway never
        // has in a cell, such as scope validation and the developer exception page.
        builder.UseEnvironment("Production");

        // The gateway project's own directory, so its appsettings.json is the one read, as the published
        // gateway reads the copy beside it.
        builder.UseContentRoot(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Gateway"));

        // UseSetting rather than ConfigureAppConfiguration: settings given this way are in the
        // configuration when Program.cs runs its Add* entry points, some of which read configuration
        // while they register (AddPlatformTelemetry reads OTEL_EXPORTER_OTLP_ENDPOINT, for one).
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        // After Program.cs has registered everything, so a replacement wins over a TryAdd and an Add alike.
        builder.ConfigureTestServices(services);
    }
}
