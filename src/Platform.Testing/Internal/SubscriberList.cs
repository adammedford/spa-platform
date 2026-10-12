namespace Platform.Testing.Internal;

/// <summary>
/// A list of callbacks that each unsubscribe by disposing. Disposing twice is not an error, and the
/// list may be read while a subscriber is running.
/// </summary>
internal sealed class SubscriberList<T>
    where T : notnull
{
    private readonly object gate = new();
    private readonly List<T> subscribers = [];

    public IDisposable Add(T subscriber)
    {
        lock (gate)
        {
            subscribers.Add(subscriber);
        }

        return new Subscription(this, subscriber);
    }

    /// <summary>A copy, so a subscriber may unsubscribe while the list is being walked.</summary>
    public T[] Snapshot()
    {
        lock (gate)
        {
            return [.. subscribers];
        }
    }

    private void Remove(T subscriber)
    {
        lock (gate)
        {
            subscribers.Remove(subscriber);
        }
    }

    private sealed class Subscription(SubscriberList<T> owner, T subscriber) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.Remove(subscriber);
        }
    }
}
