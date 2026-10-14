using MockApi;

// The mock API's entry point. Two listeners in one process, configured from the task environment,
// which is what task 1.4 supplies: "the mock API container joins once its image exists, with its
// client allowlists set from the client ids of apps 1 and 2".
//
// Everything this reads has to come from configuration rather than from a fixture file, because a
// client id is created by Terraform and known to nobody before that.

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

string Required(string key) =>
    configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"{key} is not set. The mock API needs the issuer, its metadata address and one client id per API.");

string issuer = Required("MockApi:Issuer");
string metadata = configuration["MockApi:MetadataAddress"] is { Length: > 0 } address
    ? address
    : issuer.TrimEnd('/') + "/.well-known/openid-configuration";

// The two listeners the plan fixes, and only those. A task environment that named its own would be
// a second configuration path with nothing reading it, and the row fixes the pair.
MockApiOptions options = MockApiOptions.Default(
    issuer, metadata, Required("MockApi:App1Client"), Required("MockApi:App2Client"));

await using MockApiHost host = await MockApiHost.StartAsync(options, CancellationToken.None);

foreach (ApiListenerOptions listener in options.Listeners)
{
    Console.WriteLine($"{listener.Name} listening on {host.BaseAddressOf(listener.Name)}, " +
                      $"audience {listener.Audience ?? "(none)"}, " +
                      $"clients {string.Join(", ", host.AdmissionOf(listener.Name).Clients)}");
}

await host.WaitForShutdownAsync();
