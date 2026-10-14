using Amazon.SimpleSystemsManagement;
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
}
