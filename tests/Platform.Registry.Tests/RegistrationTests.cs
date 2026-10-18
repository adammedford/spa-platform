using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// <c>AddPlatformRegistry</c> is workstream 2's service entry point (<c>contracts/pipeline.md</c> ->
/// Service registration). Task 2.5's part: the gateway's <see cref="IClientSecretSource"/> is the
/// Parameter Store one, over whichever SDK client the container already holds.
/// </summary>
public sealed class RegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_secret_source_is_parameter_store_over_the_registered_sdk_client()
    {
        StubParameterStore ssm = new();
        ssm.Put("/spa-platform/poc/apps/app1/client-secret", 1, "s3cret");
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAmazonSimpleSystemsManagement>(ssm);

        builder.AddPlatformRegistry();
        using IHost host = builder.Build();
        IClientSecretSource source = host.Services.GetRequiredService<IClientSecretSource>();
        await source.PreloadAsync(["ssm:/spa-platform/poc/apps/app1/client-secret:1"], Ct);

        Assert.IsType<ParameterStoreClientSecretSource>(source);
        Assert.Same(source, host.Services.GetRequiredService<IClientSecretSource>());
        Assert.Equal("s3cret", source.Get("ssm:/spa-platform/poc/apps/app1/client-secret:1"));
        Assert.Single(ssm.Requests);
    }

    [Fact]
    public void Without_a_registered_client_one_sdk_client_is_registered_and_not_yet_built()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.AddPlatformRegistry();

        ServiceDescriptor client = Assert.Single(builder.Services,
            d => d.ServiceType == typeof(IAmazonSimpleSystemsManagement));
        Assert.Equal(ServiceLifetime.Singleton, client.Lifetime);
        Assert.NotNull(client.ImplementationFactory);
    }

    [Fact]
    public void The_registry_is_one_live_registry_and_starts_with_nothing_live()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();

        builder.AddPlatformRegistry();
        using IHost host = builder.Build();

        IRegistry registry = host.Services.GetRequiredService<IRegistry>();
        Assert.IsType<LiveRegistry>(registry);
        Assert.Same(registry, host.Services.GetRequiredService<IRegistry>());
        Assert.Same(LiveRegistry.NoSnapshot, registry.Current);
    }

    [Fact]
    public void The_object_store_is_s3_over_the_registry_bucket()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddJsonWithoutFileProvider(Examples.PathOf("platform-config.example.json"));
        builder.Services.AddSingleton<IAmazonS3>(new AmazonS3Client(
            new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1));

        builder.AddPlatformRegistry();
        using IHost host = builder.Build();

        Assert.IsType<S3ObjectStore>(host.Services.GetRequiredService<IObjectStore>());
        ServiceDescriptor client = Assert.Single(builder.Services, d => d.ServiceType == typeof(IAmazonS3));
        Assert.Null(client.ImplementationFactory);
    }

    [Fact]
    public void Platform_policy_is_read_from_configuration()
    {
        // Read against the contract's own example, so a change to it reaches this test.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddJsonWithoutFileProvider(Examples.PathOf("platform-config.example.json"));

        builder.AddPlatformRegistry();
        using IHost host = builder.Build();
        PlatformPolicy policy = host.Services.GetRequiredService<PlatformPolicy>();
        PlatformPolicy expected = Examples.Policy();

        Assert.Equal(expected.AllowedHostSuffixes, policy.AllowedHostSuffixes);
        Assert.Equal(expected.AllowedUpstreams, policy.AllowedUpstreams);
        Assert.Equal(expected.Issuers.Keys.Order(), policy.Issuers.Keys.Order());
        foreach ((string name, IssuerDefinition issuer) in expected.Issuers)
        {
            Assert.Equal(issuer, policy.Issuers[name]);
        }
    }

    [Fact]
    public void With_no_configuration_the_policy_allows_nothing()
    {
        // Fail closed: with no Policy:* every host suffix, upstream and issuer is refused, so no entry
        // can validate.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.AddPlatformRegistry();
        using IHost host = builder.Build();
        PlatformPolicy policy = host.Services.GetRequiredService<PlatformPolicy>();

        Assert.Empty(policy.AllowedHostSuffixes);
        Assert.Empty(policy.AllowedUpstreams);
        Assert.Empty(policy.Issuers);
    }
}
