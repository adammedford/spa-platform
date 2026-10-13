using System.Collections;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IClientSecretSource"/> over a dictionary a test fills in. Set the values a
/// reference resolves to with the indexer, then preload; a reference with no value set stands for a
/// parameter that cannot be read.
///
/// <code>
/// FakeClientSecretSource secrets = new() { ["ssm:/.../client-secret:1"] = "s3cret" };
/// await secrets.PreloadAsync([entry.OAuth.ClientSecretRef], ct);
/// </code>
///
/// The collection initializer is why this implements <see cref="IEnumerable"/>; nothing enumerates
/// it, and enumerating it would be a way to spill every secret into a log.
/// </summary>
public sealed class FakeClientSecretSource : IClientSecretSource, IEnumerable
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> preloaded = new(StringComparer.Ordinal);
    private readonly List<string> preloadCalls = [];

    /// <summary>Sets the value a reference resolves to. A reference left unset cannot be read.</summary>
    public string this[string secretRef]
    {
        set => values[secretRef] = value;
    }

    /// <summary>Every reference <see cref="PreloadAsync"/> was asked for, in order.</summary>
    public IReadOnlyList<string> Preloaded => preloadCalls;

    public Task PreloadAsync(IEnumerable<string> secretRefs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (string secretRef in secretRefs)
        {
            preloadCalls.Add(secretRef);
            preloaded.Add(secretRef);
        }

        return Task.CompletedTask;
    }

    public string Get(string secretRef)
    {
        if (!preloaded.Contains(secretRef))
        {
            throw new InvalidOperationException(
                $"'{secretRef}' was not preloaded. Get does no I/O: preload a reference before reading it.");
        }

        if (!values.TryGetValue(secretRef, out string? value))
        {
            throw new InvalidOperationException(
                $"'{secretRef}' could not be read. The app it belongs to is left out of the snapshot.");
        }

        return value;
    }

    IEnumerator IEnumerable.GetEnumerator() =>
        throw new NotSupportedException("A secret source is not enumerable. Read one reference at a time.");
}
