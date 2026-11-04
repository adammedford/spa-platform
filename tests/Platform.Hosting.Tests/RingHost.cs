using System.Xml.Linq;
using Amazon.SimpleSystemsManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// A host built by <c>AddPlatformHosting</c> and nothing else of the gateway's, for the key ring
/// (plan row P2-2). Configuration is the test's alone; a Parameter Store client, when given, is
/// registered before the call, as <c>AddPlatformRegistry</c> registers the gateway's
/// (contracts/pipeline.md, Service registration). The pipeline is empty: the ring is read through
/// <see cref="IDataProtectionProvider"/>, which is what the cookie, state and antiforgery protectors
/// read it through.
/// </summary>
internal static class RingHost
{
    public const string Purpose = "Platform.Hosting.Tests.Ring";

    public const string Prefix = "/spa-platform/test/data-protection/";

    public const string KeyId = "alias/spa-platform-test";

    /// <summary>The section, a cell and the memory session store: a cell's configuration.</summary>
    public static Dictionary<string, string?> WithSection(string cell = "test") => new()
    {
        ["Platform:Cell"] = cell,
        ["DataProtection:ParameterPrefix"] = Prefix,
        ["DataProtection:KmsKeyId"] = KeyId,
        ["Session:Store"] = "memory",
    };

    /// <summary>
    /// The client as an instance, which the container does not own; <paramref name="ownedClient"/>
    /// registers it through a factory instead, so the container owns and disposes it.
    /// </summary>
    public static Task<StageHost> StartAsync(
        IDictionary<string, string?> configuration,
        IAmazonSimpleSystemsManagement? client,
        CancellationToken ct,
        Action<IServiceCollection>? after = null,
        bool ownedClient = false) =>
        StageHost.StartAsync(
            new StageHostOptions
            {
                Services = builder =>
                {
                    builder.Configuration.AddInMemoryCollection(configuration);
                    if (client is not null)
                    {
                        if (ownedClient)
                        {
                            builder.Services.AddSingleton(_ => client);
                        }
                        else
                        {
                            builder.Services.AddSingleton(client);
                        }
                    }

                    builder.AddPlatformHosting();
                    after?.Invoke(builder.Services);
                },
                Pipeline = _ => { },
            },
            ct);

    /// <summary>
    /// For a host with no section: the framework's ring over a repository of this host's own, so two
    /// such hosts cannot share the user-profile ring by accident (change request P2-2, decision 6).
    /// </summary>
    public static void OwnRepository(IServiceCollection services)
    {
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new ListRepository());
    }

    public static string Protect(StageHost host, string payload) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(payload);

    public static string Unprotect(StageHost host, string protectedPayload) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedPayload);

    /// <summary>An <see cref="IXmlRepository"/> over a list, which is what a ring is at the framework's seam.</summary>
    private sealed class ListRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (elements)
            {
                return [.. elements.Select(e => new XElement(e))];
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (elements)
            {
                elements.Add(new XElement(element));
            }
        }
    }
}
