using System.Collections.Concurrent;
using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// Every key the <b>gateway</b> asked the bucket for. N-13's second sentence is "No object outside
/// the app's release prefix is read", and the ticket's acceptance sharpens it to "refused
/// <b>before</b> any object store read is attempted" — a claim about a read that did not happen,
/// which no status code can carry.
///
/// <para>It wraps whatever <see cref="IObjectStore"/> the harness registered rather than replacing
/// it, so the cell still holds one bucket: the harness goes on writing releases and registry
/// revisions through <see cref="GatewayHarness.Store"/>, and what this records is what the gateway
/// read. It is installed through <see cref="GatewayHarnessOptions.ConfigureServices"/>, the
/// harness's own door for a test's service replacements, which runs after the harness's own
/// registrations — so the harness needs no change, the way task 6.2a's telemetry capture needs
/// none.</para>
///
/// <para>Recording a read is not reaching into a stage. The bucket is a collaborator the gateway
/// talks to over the network in the cell, the same kind of boundary as an upstream, and
/// <c>contracts/s3-layout.md</c> is the contract being held to: a key is built "never from an
/// unchecked path segment". No stage is called and no field of one is read.</para>
/// </summary>
internal sealed class ObjectStoreRecorder
{
    private readonly ConcurrentQueue<string> reads = new();
    private bool installed;

    /// <summary>Every key the gateway has asked to read, in order.</summary>
    public IReadOnlyList<string> Reads
    {
        get
        {
            if (!installed)
            {
                throw new InvalidOperationException(
                    "no harness was started with these options, so nothing could have been recorded.");
            }

            return [.. reads];
        }
    }

    /// <summary>The keys read since <paramref name="from"/>, which is a count taken earlier.</summary>
    public IReadOnlyList<string> Since(int from) => [.. Reads.Skip(from)];

    /// <summary>Harness options that record the gateway's reads, laid over any a test already has.</summary>
    public GatewayHarnessOptions Options(GatewayHarnessOptions? options = null)
    {
        options ??= new GatewayHarnessOptions();
        Action<IServiceCollection>? theirs = options.ConfigureServices;

        return options with
        {
            ConfigureServices = services =>
            {
                theirs?.Invoke(services);

                // The harness registered its own store as a singleton instance just before this
                // runs. Wrapping that instance, rather than registering a second store, is what
                // keeps the releases the harness published readable.
                ServiceDescriptor existing = services.Last(service => service.ServiceType == typeof(IObjectStore));
                IObjectStore store = existing.ImplementationInstance as IObjectStore
                    ?? throw new InvalidOperationException(
                        "the harness no longer registers IObjectStore as an instance, so this recorder cannot wrap it.");

                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(new Recording(this, store));
                installed = true;
            },
        };
    }

    private sealed class Recording(ObjectStoreRecorder recorder, IObjectStore inner) : IObjectStore
    {
        public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
        {
            recorder.reads.Enqueue(key);
            return inner.GetAsync(key, ifNoneMatch, ct);
        }

        public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
            inner.PutAsync(key, body, ifMatch, ct);
    }
}
