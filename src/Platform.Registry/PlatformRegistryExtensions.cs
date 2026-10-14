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
    public static IHostApplicationBuilder AddPlatformRegistry(this IHostApplicationBuilder builder)
    {
        IServiceCollection services = builder.Services;

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
}
