using System.Collections.Immutable;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// Workstream 2's registry registration, from the service table in <c>contracts/pipeline.md</c>.
///
/// Task 2.5 registers the Parameter Store <see cref="IClientSecretSource"/>. Task 2.2 registers the S3
/// object store, <see cref="PlatformPolicy"/> from configuration, the live <see cref="IRegistry"/>, and
/// the poller that fills it. Keep this signature and add no others.
///
/// Task 2.1 registers nothing here: <see cref="RegistryValidator"/> is a static, pure function of the
/// document, the policy and the live revision, so the registry source calls it directly and
/// <c>spactl</c> does the same without a container.
///
/// Every registration is a TryAdd, so a test or a later task supplies its own first.
/// </summary>
public static class PlatformRegistryExtensions
{
    /// <summary>The category the start-up check's Error lines are logged under (#1400).</summary>
    public const string ConfigurationCategory = "Platform.Registry.Configuration";

    public static IHostApplicationBuilder AddPlatformRegistry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;

        // First, so nothing is registered over a configuration the schema refuses (ADR-0042 decision 1).
        // Here and not in AddPlatformHosting: the engine and the embedded schemas are in this project,
        // and A-03 keeps Platform.Hosting from referencing it.
        RefuseAnInvalidConfiguration(builder);

        // The SDK clients take their region and credentials from the environment: the ECS task role and
        // AWS_REGION in the cell, a profile on a workstation (the SDK's default chains,
        // https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html). Each is built on
        // first use, not here, because its constructor throws when no region is configured.
        services.TryAddSingleton<IAmazonSimpleSystemsManagement>(_ => new AmazonSimpleSystemsManagementClient());
        services.TryAddSingleton<IAmazonS3>(_ => new AmazonS3Client());

        // One instance for the process: the cache is the point (plan row 2.5).
        services.TryAddSingleton<IClientSecretSource, ParameterStoreClientSecretSource>();

        // Registry:Bucket. Phase 1 has one bucket for the registry and every release
        // (platform-config.schema.json, Assets:Bucket), so this store also reaches the assets task 2.4
        // serves; a second bucket would be a second store.
        services.AddOptions<RegistrySourceOptions>().Bind(builder.Configuration.GetSection("Registry"));
        services.TryAddSingleton<IObjectStore>(provider => new S3ObjectStore(
            provider.GetRequiredService<IAmazonS3>(),
            provider.GetRequiredService<IOptions<RegistrySourceOptions>>().Value.Bucket!));

        services.TryAddSingleton(_ => PlatformPolicyConfiguration.Read(builder.Configuration));
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<LiveRegistry>();
        services.TryAddSingleton<IRegistry>(provider => provider.GetRequiredService<LiveRegistry>());
        services.TryAddSingleton(provider => new RegistrySource(
            provider.GetRequiredService<IObjectStore>(),
            provider.GetRequiredService<IClientSecretSource>(),
            provider.GetRequiredService<PlatformPolicy>(),
            provider.GetRequiredService<LiveRegistry>(),
            provider.GetRequiredService<IOptions<RegistrySourceOptions>>().Value.Key!,
            provider.GetRequiredService<ILogger<RegistrySource>>()));
        services.AddHostedService<RegistryPoller>();

        return builder;
    }

    /// <summary>
    /// Validates the configuration and, on any finding, logs one Error line per finding under
    /// <see cref="ConfigurationCategory"/>, naming its key and never its value, then throws, so the
    /// process stops before it serves (ADR-0042). A configuration the schema refuses is not a runtime
    /// condition the way an unreadable registry is: <see cref="RegistryPoller"/> still never stops the host.
    ///
    /// No host exists yet, so the lines go through a provider built over the services registered so far,
    /// which in the gateway hold <c>AddPlatformTelemetry</c>'s console logger. That provider is disposed
    /// before the throw: the console logger writes from a background queue, and its processor's
    /// <c>Dispose</c> drains that queue before it returns
    /// (https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Logging.Console/src/ConsoleLoggerProcessor.cs),
    /// so the lines reach stdout before the process ends; the gateway run of #1400 shows them there. A
    /// valid configuration builds nothing.
    /// </summary>
    private static void RefuseAnInvalidConfiguration(IHostApplicationBuilder builder)
    {
        ImmutableArray<PlatformConfigurationFinding> findings = PlatformConfiguration.Findings(builder.Configuration);
        if (findings.IsEmpty)
        {
            return;
        }

        using (ServiceProvider provider = builder.Services.BuildServiceProvider())
        {
            if (provider.GetService<ILoggerFactory>()?.CreateLogger(ConfigurationCategory) is { } logger)
            {
                foreach (PlatformConfigurationFinding finding in findings)
                {
                    logger.LogError(
                        "The platform configuration fails contracts/platform-config.schema.json at {Key}: {Keyword}. The gateway does not start (ADR-0042).",
                        finding.Key,
                        finding.Keyword.Length == 0 ? "schema" : finding.Keyword);
                }
            }
        }

        throw new InvalidOperationException(
            "The platform configuration fails contracts/platform-config.schema.json at " +
            string.Join(", ", findings.Select(finding => finding.ToString())) +
            $". Each finding is an Error line under {ConfigurationCategory}; the gateway does not start (ADR-0042).");
    }
}
