using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// The gateway's <see cref="IRegistry"/> (plan row 2.2). It holds the live snapshot and nothing else:
/// <see cref="RegistrySource"/> decides what goes live, and this replaces the reference, then notifies.
///
/// Kept apart from the source because it has no dependencies, so it can be built at start-up by
/// anything that reads the registry (YARP's configuration provider does, task 4.1) even when the
/// AWS clients behind the source cannot be built: their constructors throw with no region configured,
/// which is how CI's G1a smoke test starts the gateway.
/// </summary>
public sealed class LiveRegistry : IRegistry
{
    /// <summary>
    /// What <see cref="Current"/> is until the first valid snapshot: revision 0 and no apps, so every
    /// host is unknown. A registry document's revision is at least 1 (<c>registry.schema.json</c>), so
    /// revision 0 means "nothing is live", and <c>/readyz</c> answers 503 on it (contracts/pipeline.md,
    /// Health endpoints; task 2.3).
    /// </summary>
    public static readonly RegistrySnapshot NoSnapshot = new(
        Revision: 0,
        ETag: "",
        FrozenDictionary<string, AppDefinition>.Empty,
        FrozenDictionary<string, AppDefinition>.Empty);

    private readonly ILogger<LiveRegistry> logger;
    private readonly object subscribing = new();

    // Replaced whole under the lock and read without it, so Swap walks a stable copy while a subscriber
    // unsubscribes.
    private Action<RegistrySnapshot, RegistrySnapshot>[] subscribers = [];
    private RegistrySnapshot current = NoSnapshot;

    public LiveRegistry(ILogger<LiveRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <inheritdoc />
    public RegistrySnapshot Current => Volatile.Read(ref current);

    /// <inheritdoc />
    public IDisposable OnSwapped(Action<RegistrySnapshot, RegistrySnapshot> oldThenNew)
    {
        ArgumentNullException.ThrowIfNull(oldThenNew);
        lock (subscribing)
        {
            subscribers = [.. subscribers, oldThenNew];
        }

        return new Subscription(this, oldThenNew);
    }

    /// <summary>
    /// Replaces the snapshot, then notifies: swap, then evict (GLOSSARY.md, Swap; docs/plan/01-plan.md,
    /// OptionsInvalidator). The snapshot is immutable and replaced by one reference write, so a reader
    /// sees the old one or the new one and never a mixture. A subscriber that throws is logged and
    /// does not stop the others: the swap has already happened, and an evictor left unrun would keep
    /// stale options for its own app only.
    /// </summary>
    internal void Swap(RegistrySnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        RegistrySnapshot previous = Interlocked.Exchange(ref current, next);

        foreach (Action<RegistrySnapshot, RegistrySnapshot> subscriber in Volatile.Read(ref subscribers))
        {
            try
            {
                subscriber(previous, next);
            }
            catch (Exception ex)
            {
                // The type and message, not the exception: a subscriber's exception is not ours to vouch
                // for (contracts/telemetry.md, Never logged).
                logger.LogError(
                    "A registry subscriber failed after the swap to revision {Revision}: {Error}: {Message}",
                    next.Revision, ex.GetType().Name, ex.Message);
            }
        }
    }

    private void Remove(Action<RegistrySnapshot, RegistrySnapshot> subscriber)
    {
        lock (subscribing)
        {
            int index = Array.IndexOf(subscribers, subscriber);
            if (index >= 0)
            {
                subscribers = [.. subscribers[..index], .. subscribers[(index + 1)..]];
            }
        }
    }

    private sealed class Subscription(LiveRegistry owner, Action<RegistrySnapshot, RegistrySnapshot> subscriber) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Remove(subscriber);
            }
        }
    }
}
