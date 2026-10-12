using System.Collections.Frozen;
using Platform.Abstractions;
using Platform.Testing.Internal;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IRegistry"/> a test drives by hand: construct it with a snapshot and call
/// <see cref="Swap"/> to move it on. Every workstream that reacts to a registry change builds
/// against this rather than against the S3-backed source of task 2.2.
/// </summary>
public sealed class InMemoryRegistry : IRegistry
{
    private readonly SubscriberList<Action<RegistrySnapshot, RegistrySnapshot>> subscribers = new();

    public InMemoryRegistry(RegistrySnapshot current) => Current = current;

    public RegistrySnapshot Current { get; private set; }

    public IDisposable OnSwapped(Action<RegistrySnapshot, RegistrySnapshot> oldThenNew) =>
        subscribers.Add(oldThenNew);

    /// <summary>Replaces the snapshot, then notifies. Never the other way round.</summary>
    public void Swap(RegistrySnapshot next)
    {
        RegistrySnapshot previous = Current;
        Current = next;

        foreach (Action<RegistrySnapshot, RegistrySnapshot> subscriber in subscribers.Snapshot())
        {
            subscriber(previous, next);
        }
    }

    /// <summary>Builds a snapshot from entries, indexed both ways, for a test that has definitions.</summary>
    public static RegistrySnapshot SnapshotOf(long revision, string etag, IEnumerable<AppDefinition> apps)
    {
        AppDefinition[] entries = [.. apps];

        return new RegistrySnapshot(
            revision,
            etag,
            entries.ToFrozenDictionary(app => app.Host, StringComparer.OrdinalIgnoreCase),
            entries.ToFrozenDictionary(app => app.AppId, StringComparer.Ordinal));
    }
}
