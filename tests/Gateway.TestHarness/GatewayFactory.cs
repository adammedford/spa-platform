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
    private readonly DirectoryInfo contentRoot;

    public GatewayFactory(IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection> services)
    {
        this.settings = settings;
        this.services = services;
        contentRoot = CopyOfTheGatewaySettings();

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

        // A directory holding a copy of the gateway project's own settings files, so its appsettings.json
        // is the one read, as the published gateway reads the copy beside it (see CopyOfTheGatewaySettings).
        builder.UseContentRoot(contentRoot.FullName);

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

    protected override void Dispose(bool disposing)
    {
        // DisposeAsync ends here too, after the host has stopped, so nothing still holds a file open.
        base.Dispose(disposing);
        if (disposing && contentRoot.Exists)
        {
            contentRoot.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A new temporary directory holding a copy of every <c>appsettings*.json</c> in <c>src/Gateway</c>,
    /// for the gateway's host to use as its content root (issues #75 and #138).
    ///
    /// <para>Not <c>src/Gateway</c> itself. The host reads <c>appsettings.json</c> through a
    /// <c>PhysicalFileProvider</c> rooted at the content root
    /// (https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration.fileconfigurationsource.resolvefileprovider),
    /// and that provider's default <c>ExclusionFilters.Sensitive</c> skips hidden files
    /// (https://learn.microsoft.com/dotnet/api/microsoft.extensions.fileproviders.physical.exclusionfilters).
    /// macOS flags every file under <c>.claude/worktrees/</c> hidden, and the file is optional to the
    /// host, so there the gateway started without its logging levels and nothing said so.</para>
    ///
    /// <para>The copy is written from the file's bytes, not with <c>File.Copy</c>, which on macOS may
    /// clone the file and keep its flags. The host still reads the settings itself, through its own
    /// provider and in its own precedence, so a test that reads a setting from the file proves the
    /// gateway read it.</para>
    /// </summary>
    private static DirectoryInfo CopyOfTheGatewaySettings()
    {
        string source = Path.Combine(FixtureFiles.RepositoryRoot, "src", "Gateway");
        DirectoryInfo copy = Directory.CreateTempSubdirectory("gateway-content-root-");
        foreach (string file in Directory.EnumerateFiles(source, "appsettings*.json"))
        {
            File.WriteAllBytes(Path.Combine(copy.FullName, Path.GetFileName(file)), File.ReadAllBytes(file));
        }

        return copy;
    }
}
