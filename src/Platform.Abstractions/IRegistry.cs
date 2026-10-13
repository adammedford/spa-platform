using System.Collections.Frozen;

namespace Platform.Abstractions;

/// <summary>
/// One validated registry document, indexed for lookup. Source: docs/plan/02-contracts.md ->
/// Interfaces. Freezes at G1a.
/// </summary>
/// <param name="Revision">The document's revision, which only ever rises.</param>
/// <param name="ETag">The ETag of the S3 object the snapshot was built from.</param>
public sealed record RegistrySnapshot(
    long Revision,
    string ETag,
    FrozenDictionary<string, AppDefinition> ByHost,
    FrozenDictionary<string, AppDefinition> ByAppId);

/// <summary>
/// The live registry. <see cref="Current"/> is replaced whole, never mutated, so a reader that has
/// taken a reference keeps a consistent view for the rest of its request.
/// </summary>
public interface IRegistry
{
    RegistrySnapshot Current { get; }

    /// <summary>
    /// Raised after the swap, never before: <see cref="Current"/> is already the new snapshot when a
    /// subscriber runs. Subscribers evict caches here (task 3.6 swaps before it evicts). Dispose the
    /// result to unsubscribe.
    /// </summary>
    IDisposable OnSwapped(Action<RegistrySnapshot, RegistrySnapshot> oldThenNew);
}
