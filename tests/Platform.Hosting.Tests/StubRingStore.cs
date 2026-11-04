using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Platform.Hosting.Tests;

/// <summary>
/// Parameter Store without the network, for the key ring (change request P2-2, decision 6). The SDK's
/// generated operations are <c>virtual</c>, so this overrides the two the ring sends,
/// <c>GetParametersByPathAsync</c> and <c>PutParameterAsync</c>, and the client-secret read,
/// <c>GetParameterAsync</c>, which the decorator must pass through. Any other operation goes to the
/// real client and fails for want of credentials. Anonymous credentials and a fixed region mean
/// constructing it does no I/O.
///
/// A disposed stub throws on every operation, as a disposed client would, so "still usable after the
/// ring's repository is disposed" can fail.
/// </summary>
internal sealed class StubRingStore()
    : AmazonSimpleSystemsManagementClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
{
    private readonly ConcurrentDictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AmazonWebServiceRequest> requests = new();
    private volatile bool disposed;

    /// <summary>Every request any caller sent, in order.</summary>
    public IReadOnlyList<AmazonWebServiceRequest> Requests => [.. requests];

    public IReadOnlyList<PutParameterRequest> Puts => [.. requests.OfType<PutParameterRequest>()];

    /// <summary>When set, every path read throws what it returns (F16).</summary>
    public Func<Exception>? ReadFailure { get; set; }

    /// <summary>
    /// While set, every path read answers with no parameters, as if no other task had written yet:
    /// how two tasks both see an empty prefix and both mint.
    /// </summary>
    public bool HideWrites { get; set; }

    public bool Disposed => disposed;

    /// <summary>Stores a value a <c>GetParameterAsync</c> answers, as a client secret would be.</summary>
    public void Put(string name, string value) => values[name] = value;

    public override Task<GetParametersByPathResponse> GetParametersByPathAsync(
        GetParametersByPathRequest request, CancellationToken cancellationToken = default)
    {
        Enter(request);
        if (ReadFailure is { } failure)
        {
            throw failure();
        }

        List<Parameter> found = HideWrites
            ? []
            : [.. values
                .Where(pair => pair.Key.StartsWith(request.Path, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new Parameter { Name = pair.Key, Value = pair.Value, Type = ParameterType.SecureString })];

        return Task.FromResult(new GetParametersByPathResponse { Parameters = found });
    }

    public override Task<PutParameterResponse> PutParameterAsync(
        PutParameterRequest request, CancellationToken cancellationToken = default)
    {
        Enter(request);
        values[request.Name] = request.Value;
        return Task.FromResult(new PutParameterResponse { Version = 1, Tier = request.Tier });
    }

    public override Task<GetParameterResponse> GetParameterAsync(
        GetParameterRequest request, CancellationToken cancellationToken = default)
    {
        Enter(request);
        if (!values.TryGetValue(request.Name, out string? value))
        {
            throw new ParameterNotFoundException($"Parameter {request.Name} not found.");
        }

        return Task.FromResult(new GetParameterResponse
        {
            Parameter = new Parameter { Name = request.Name, Value = value, Type = ParameterType.SecureString },
        });
    }

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }

    private void Enter(AmazonWebServiceRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        requests.Enqueue(request);
    }
}
