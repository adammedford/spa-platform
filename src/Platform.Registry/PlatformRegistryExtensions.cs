using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// Workstream 2's registry registration, from the service table in <c>contracts/pipeline.md</c>.
///
/// Task 2.5 registers the Parameter Store <see cref="IClientSecretSource"/>. Task 2.2 adds the S3
/// object store and the registry source with its jittered conditional GET. Keep this signature and
/// add no others.
///
/// Task 2.1 registers nothing here: <see cref="RegistryValidator"/> is a static, pure function of the
/// document, the policy and the live revision, so the registry source calls it directly and
/// <c>spactl</c> does the same without a container.
/// </summary>
public static class PlatformRegistryExtensions
{
    public static IHostApplicationBuilder AddPlatformRegistry(this IHostApplicationBuilder builder)
    {
        // The SDK client takes its region and credentials from the environment: the ECS task role and
        // AWS_REGION in the cell, a profile on a workstation (the SDK's default chains,
        // https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html). It is built on
        // first use, not here, so a host that never reads a secret never needs either. TryAdd lets a
        // test or a later task supply its own client.
        builder.Services.TryAddSingleton<IAmazonSimpleSystemsManagement>(
            _ => new AmazonSimpleSystemsManagementClient());

        // One instance for the process: the cache is the point (plan row 2.5).
        builder.Services.TryAddSingleton<IClientSecretSource, ParameterStoreClientSecretSource>();

        return builder;
    }
}
