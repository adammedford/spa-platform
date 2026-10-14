using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// An <see cref="IObjectStore"/> that records every read and can be made to fail once, around the
/// Platform.Testing double.
/// </summary>
internal sealed class RecordingObjectStore(IObjectStore inner) : IObjectStore
{
    private readonly List<Read> reads = [];

    public IObjectStore Inner { get; } = inner;

    public IReadOnlyList<Read> Reads
    {
        get
        {
            lock (reads)
            {
                return [.. reads];
            }
        }
    }

    /// <summary>The ETag of the last object a read returned with a body.</summary>
    public string LastETag { get; private set; } = "";

    public Exception? FailNextRead { get; set; }

    public async Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        if (FailNextRead is { } failure)
        {
            FailNextRead = null;
            throw failure;
        }

        StoredObject? stored = await Inner.GetAsync(key, ifNoneMatch, ct);
        lock (reads)
        {
            reads.Add(new Read(key, ifNoneMatch, stored?.NotModified ?? false));
        }

        if (stored is { NotModified: false })
        {
            LastETag = stored.ETag;
        }

        return stored;
    }

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        Inner.PutAsync(key, body, ifMatch, ct);

    public sealed record Read(string Key, string? IfNoneMatch, bool NotModified);
}
