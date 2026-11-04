using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Gateway.TestHarness;

/// <summary>
/// Parameter Store without the network, for the cell's key ring in a harness with more than one gateway
/// host (plan rows P2-2 and P2-5; #345). The real <c>AddPlatformHosting</c> ring reads and writes its keys
/// through the client it is given, so registering one stub in every host is one ring for the cell, and
/// one stub per host is one ring per host: the control.
///
/// The SDK's generated operations are <c>virtual</c>, so this overrides the two the ring sends,
/// <c>GetParametersByPathAsync</c> and <c>PutParameterAsync</c>. Any other operation goes to the real
/// client and fails for want of credentials, which is what an unexpected call should do. Anonymous
/// credentials and a fixed region mean constructing it does no I/O.
/// </summary>
public sealed class ParameterStoreStub()
    : AmazonSimpleSystemsManagementClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
{
    private readonly ConcurrentDictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> puts = new();

    /// <summary>The name of every parameter written, in order: one per key the ring minted.</summary>
    public IReadOnlyList<string> Puts => [.. puts];

    public override Task<GetParametersByPathResponse> GetParametersByPathAsync(
        GetParametersByPathRequest request, CancellationToken cancellationToken = default)
    {
        List<Parameter> found = [.. values
            .Where(pair => pair.Key.StartsWith(request.Path, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new Parameter { Name = pair.Key, Value = pair.Value, Type = ParameterType.SecureString })];
        return Task.FromResult(new GetParametersByPathResponse { Parameters = found });
    }

    public override Task<PutParameterResponse> PutParameterAsync(
        PutParameterRequest request, CancellationToken cancellationToken = default)
    {
        values[request.Name] = request.Value;
        puts.Enqueue(request.Name);
        return Task.FromResult(new PutParameterResponse { Version = 1, Tier = request.Tier });
    }
}
