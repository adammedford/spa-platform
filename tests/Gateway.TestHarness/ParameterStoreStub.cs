using System.Collections.Concurrent;
using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Gateway.TestHarness;

/// <summary>
/// Parameter Store without the network, for two readers of the cell's store.
///
/// <list type="bullet">
/// <item>The key ring, in a harness with more than one gateway host (plan rows P2-2 and P2-5; #345). The
/// real <c>AddPlatformHosting</c> ring reads and writes its keys through the client it is given, so
/// registering one stub in every host is one ring for the cell, and one stub per host is one ring per
/// host: the control. The ring sends <c>GetParametersByPathAsync</c> and <c>PutParameterAsync</c>.</item>
/// <item>The client secrets, under <see cref="GatewayHarnessOptions.SecretsThroughParameterStore"/>
/// (#1516): one stub for the cell, read by a <c>ParameterStoreClientSecretSource</c> in each host, which
/// sends <c>GetParameterAsync</c> with the version selector <c>{name}:{version}</c>. An operator writes a
/// secret with <c>PutParameterAsync</c> and removes it with <c>DeleteParameterAsync</c>.</item>
/// </list>
///
/// Every write of a name adds a version, from 1, as Parameter Store numbers them
/// (https://docs.aws.amazon.com/systems-manager/latest/userguide/sysman-paramstore-versions.html), and a
/// delete removes the parameter with every version. A read of a name or version it does not hold throws
/// the exception the real client throws for it. The SDK's generated operations are <c>virtual</c>, so
/// this overrides the four; any other operation goes to the real client and fails for want of
/// credentials, which is what an unexpected call should do. Anonymous credentials and a fixed region mean
/// constructing it does no I/O.
/// </summary>
public sealed class ParameterStoreStub()
    : AmazonSimpleSystemsManagementClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
{
    // Each parameter's versions, the value of version n at index n - 1. Each list is guarded by its own lock.
    private readonly ConcurrentDictionary<string, List<string>> values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> puts = new();

    /// <summary>The name of every parameter written, in order: one per key the ring minted, or per secret written.</summary>
    public IReadOnlyList<string> Puts => [.. puts];

    public override Task<GetParametersByPathResponse> GetParametersByPathAsync(
        GetParametersByPathRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<Parameter> found = [.. values
            .Where(pair => pair.Key.StartsWith(request.Path, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Latest(pair.Key, pair.Value))
            .OfType<Parameter>()];
        return Task.FromResult(new GetParametersByPathResponse { Parameters = found });
    }

    /// <summary>
    /// One parameter: its newest version, or the version a <c>{name}:{version}</c> selector names
    /// (https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParameter.html).
    /// </summary>
    public override Task<GetParameterResponse> GetParameterAsync(
        GetParameterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string name = request.Name;
        long? version = null;
        int colon = name.LastIndexOf(':');
        if (colon > 0 && long.TryParse(name[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long selected))
        {
            name = name[..colon];
            version = selected;
        }

        if (!values.TryGetValue(name, out List<string>? versions))
        {
            throw NotFound(name);
        }

        lock (versions)
        {
            long at = version ?? versions.Count;
            if (versions.Count == 0)
            {
                throw NotFound(name);
            }

            if (at < 1 || at > versions.Count)
            {
                throw new ParameterVersionNotFoundException($"Version {at} of parameter {name} was not found.")
                {
                    ErrorCode = "ParameterVersionNotFound",
                };
            }

            return Task.FromResult(new GetParameterResponse
            {
                Parameter = new Parameter { Name = name, Value = versions[(int)at - 1], Version = at, Type = ParameterType.SecureString },
            });
        }
    }

    public override Task<PutParameterResponse> PutParameterAsync(
        PutParameterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new PutParameterResponse { Version = Put(request.Name, request.Value), Tier = request.Tier });
    }

    /// <summary>
    /// <see cref="PutParameterAsync"/> without the request: adds a version of <paramref name="name"/> and
    /// answers its number, for a harness that writes a secret where it cannot await.
    /// </summary>
    public long Put(string name, string value)
    {
        List<string> versions = values.GetOrAdd(name, _ => []);
        long version;
        lock (versions)
        {
            versions.Add(value);
            version = versions.Count;
        }

        puts.Enqueue(name);
        return version;
    }

    /// <summary>Removes the parameter with every version of it, as Parameter Store's delete does.</summary>
    public override Task<DeleteParameterResponse> DeleteParameterAsync(
        DeleteParameterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return values.TryRemove(request.Name, out _)
            ? Task.FromResult(new DeleteParameterResponse())
            : throw NotFound(request.Name);
    }

    private static ParameterNotFoundException NotFound(string name) =>
        new($"Parameter {name} was not found.") { ErrorCode = "ParameterNotFound" };

    private static Parameter? Latest(string name, List<string> versions)
    {
        lock (versions)
        {
            return versions.Count == 0
                ? null
                : new Parameter { Name = name, Value = versions[^1], Version = versions.Count, Type = ParameterType.SecureString };
        }
    }
}
