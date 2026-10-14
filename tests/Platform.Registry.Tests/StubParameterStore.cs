using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Platform.Registry.Tests;

/// <summary>
/// Parameter Store without the network. The SDK's generated operations are <c>virtual</c>, so this
/// overrides <c>GetParameterAsync</c> and nothing else: any other operation the source reached for
/// would go to the real client, fail for want of credentials, and fail the test. Anonymous
/// credentials and a fixed region mean constructing it does no I/O.
///
/// Parameters are keyed by name and version, as Parameter Store holds them. A request is answered
/// only when its name carries a <c>:{version}</c> selector, so a source that forgot to pin the
/// version gets <see cref="ParameterNotFoundException"/>, not the latest value.
/// </summary>
internal sealed class StubParameterStore()
    : AmazonSimpleSystemsManagementClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
{
    private readonly ConcurrentDictionary<(string Name, long Version), string> values = new();
    private readonly ConcurrentDictionary<string, Func<Exception>> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<GetParameterRequest> requests = new();

    /// <summary>Every request the source sent, in order.</summary>
    public IReadOnlyList<GetParameterRequest> Requests => [.. requests];

    /// <summary>Stores a SecureString value under a name and version.</summary>
    public void Put(string name, long version, string value) => values[(name, version)] = value;

    /// <summary>Makes every read of this exact request name throw.</summary>
    public void FailWith(string requestName, Func<Exception> failure) => failures[requestName] = failure;

    /// <summary>Answers a pinned read with this version instead of the one asked for.</summary>
    public long? VersionOverride { get; set; }

    public override Task<GetParameterResponse> GetParameterAsync(GetParameterRequest request,
        CancellationToken cancellationToken = default)
    {
        requests.Enqueue(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (failures.TryGetValue(request.Name, out Func<Exception>? failure))
        {
            throw failure();
        }

        int colon = request.Name.LastIndexOf(':');
        if (colon < 0 || !long.TryParse(request.Name[(colon + 1)..], out long version))
        {
            throw new ParameterNotFoundException($"Parameter {request.Name} not found.");
        }

        string name = request.Name[..colon];
        if (!values.TryGetValue((name, version), out string? value))
        {
            throw new ParameterVersionNotFoundException($"Version {version} of {name} not found.");
        }

        // Parameter Store returns a SecureString encrypted unless WithDecryption is set.
        string returned = request.WithDecryption == true ? value : "AQICAHencrypted-blob";

        return Task.FromResult(new GetParameterResponse
        {
            Parameter = new Parameter
            {
                Name = name,
                Version = VersionOverride ?? version,
                Type = ParameterType.SecureString,
                Value = returned,
                Selector = $":{version}",
            },
        });
    }
}
