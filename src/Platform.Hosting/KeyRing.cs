using Amazon.SimpleSystemsManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Hosting;

/// <summary>
/// The cell's data-protection key ring (GLOSSARY.md, Key ring; ADR-0025; plan row P2-2), registered by
/// <c>AddPlatformHosting</c> when a <c>DataProtection</c> section is present and not at all when it is
/// absent (change request P2-2, decision 5).
///
/// The ring is the package's: keys under <c>DataProtection:ParameterPrefix</c> in Parameter Store, as
/// <c>SecureString</c>s under <c>DataProtection:KmsKeyId</c> or the account's default key, in the
/// standard tier (decision 13: no key encryptor, so no configuration key for the tier). It writes
/// through <see cref="RingTaggingClient"/> over the client <c>AddPlatformRegistry</c> registered.
/// </summary>
internal static class KeyRing
{
    /// <summary>
    /// The application name is this and the cell, never configurable (ADR-0025 decision 2): renaming it
    /// unprotects every live cookie, login state and stored ticket at once.
    /// </summary>
    public const string ApplicationNamePrefix = "spa-platform-";

    /// <summary>The platform tag's value (contracts/resource-tags.md).</summary>
    public const string PlatformTag = "spa-platform";

    /// <summary>What applies the ring's tags (#274): the gateway, at run time, not Terraform.</summary>
    public const string ManagedByTag = "gateway";

    public const string LogCategory = "Platform.Hosting.KeyRing";

    /// <summary>The inner client's key once the decorator takes the unkeyed registration.</summary>
    private static readonly object InnerClientKey = new();

    public static string ApplicationName(string cell) => ApplicationNamePrefix + cell;

    public static void Register(IHostApplicationBuilder builder)
    {
        IConfiguration configuration = builder.Configuration;
        IServiceCollection services = builder.Services;
        IConfigurationSection section = configuration.GetSection("DataProtection");

        if (!section.Exists())
        {
            // Change request P2-2, decision 16: a durable session store under a per-task ring writes
            // tickets no other task, and no restarted task, can read.
            if (string.Equals(configuration["Session:Store"], "dynamodb", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Session:Store is 'dynamodb' and there is no DataProtection section. Durable sessions need the "
                    + "cell's shared key ring: set DataProtection:ParameterPrefix.");
            }

            services.AddHostedService(provider => new KeyRingStartup(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory), provider, prefix: null));
            return;
        }

        string? cell = configuration["Platform:Cell"];
        if (string.IsNullOrWhiteSpace(cell))
        {
            // Decision 4: an unnamed application protects under the framework's default discriminator.
            throw new InvalidOperationException(
                "A DataProtection section is set and Platform:Cell is not. The key ring's application name is "
                + ApplicationNamePrefix + "{Platform:Cell}.");
        }

        string? prefix = section["ParameterPrefix"];
        if (string.IsNullOrWhiteSpace(prefix))
        {
            // Decision 15: the schema cannot require it until the next gate, so the code does.
            throw new InvalidOperationException(
                "A DataProtection section is set and DataProtection:ParameterPrefix is not. The key ring needs the "
                + "Parameter Store prefix it lives under.");
        }

        // An empty key id is not a key id; the schema refuses one at the next gate (decision 15), and the
        // package reads it as the account's default key either way.
        string? keyId = section["KmsKeyId"];
        keyId = string.IsNullOrWhiteSpace(keyId) ? null : keyId;

        // Before the package's call, so its TryAddAWSService finds the decorator and adds no client of its
        // own (#216).
        DecorateClient(services, prefix, cell);

        services.AddDataProtection()
            .SetApplicationName(ApplicationName(cell))
            .PersistKeysToAWSSystemsManager(prefix, options => options.KMSKeyId = keyId);

        services.AddHostedService(provider => new KeyRingStartup(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory), provider, prefix));
    }

    /// <summary>
    /// Moves the client registration <c>AddPlatformRegistry</c> made to a key of this class's, where the
    /// container still owns and disposes what it builds, and registers the decorator in its place, so
    /// the package and the registry's secret source both resolve the decorator.
    /// </summary>
    private static void DecorateClient(IServiceCollection services, string prefix, string cell)
    {
        Type type = typeof(IAmazonSimpleSystemsManagement);
        ServiceDescriptor[] registered = [.. services.Where(d => d.ServiceType == type && !d.IsKeyedService)];
        if (registered.Length == 0)
        {
            throw new InvalidOperationException(
                "A DataProtection section is set and no IAmazonSimpleSystemsManagement is registered. "
                + "AddPlatformRegistry registers the client the key ring writes through, and runs before "
                + "AddPlatformHosting (contracts/pipeline.md, Service registration).");
        }

        foreach (ServiceDescriptor descriptor in registered)
        {
            services.Remove(descriptor);
        }

        // The last registration is the one GetService would have resolved.
        ServiceDescriptor last = registered[^1];
        services.Add(last switch
        {
            { ImplementationInstance: { } instance } => new ServiceDescriptor(type, InnerClientKey, instance),
            { ImplementationFactory: { } factory } =>
                new ServiceDescriptor(type, InnerClientKey, (provider, _) => factory(provider), last.Lifetime),
            _ => new ServiceDescriptor(type, InnerClientKey, last.ImplementationType!, last.Lifetime),
        });

        services.AddSingleton(provider => RingTaggingClient.Create(
            provider.GetRequiredKeyedService<IAmazonSimpleSystemsManagement>(InnerClientKey), prefix, cell));
    }
}

/// <summary>
/// Reads the ring before the gateway serves (#332, F16): a task whose prefix is unreachable or refused
/// fails its start and ECS replaces it, rather than minting a ring of its own; <c>/readyz</c> keeps its
/// contract. An empty prefix is the bootstrap and mints the first key. In <c>StartingAsync</c>, which
/// runs before every hosted service's start and so before the server listens.
///
/// The read is a protect under a purpose of its own, which loads the current ring through the
/// framework and, on an empty prefix, creates its first key by the framework's own policy. The
/// framework's own start-up read logs a failure and carries on, so it cannot stand in for this one.
///
/// With no section, it writes the one line that says the ring is the framework's default.
/// </summary>
internal sealed class KeyRingStartup(ILogger log, IServiceProvider services, string? prefix) : IHostedLifecycleService
{
    internal const string Purpose = "Platform.Hosting.KeyRing.Startup";

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (prefix is null)
        {
            log.LogInformation(
                "No DataProtection section: the key ring is the framework's default, one per task, so cookies and "
                + "logins do not survive the task.");
            return Task.CompletedTask;
        }

        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect([]);
        log.LogInformation("Key ring read from Parameter Store under {Prefix}.", prefix);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
