using System.Collections.Concurrent;
using System.Text;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// <see cref="InMemoryObjectStore"/> with every key read recorded, so a test can say what the asset
/// endpoint asked the bucket for and not only what it answered (N-13: no object outside the app's
/// release prefix is read). Seeding goes through the inner store's own write-once path.
///
/// <para>A missing key reads as the store is built to answer it, null or
/// <see cref="ObjectReadRefusedException"/>, the two modes of the shared contract (change request
/// contracts/changes/issue-381-object-read-refusal.md, item 2). <see cref="Refuse"/> refuses a key
/// that is there, as S3 refuses an existing key a role may not read, and <see cref="FailWith"/>
/// makes every read throw some other failure, which the store would let propagate.</para>
/// </summary>
internal sealed class RecordingObjectStore(MissingKeyAnswer missingKeys = MissingKeyAnswer.ConfirmedAbsence) : IObjectStore
{
    private readonly InMemoryObjectStore inner = new(missingKeys);
    private readonly ConcurrentQueue<(string Key, string? IfNoneMatch)> reads = new();
    private readonly ConcurrentDictionary<string, bool> refused = new(StringComparer.Ordinal);

    public IReadOnlyList<string> KeysRead => [.. reads.Select(read => read.Key)];

    public IReadOnlyList<string?> IfNoneMatchSent => [.. reads.Select(read => read.IfNoneMatch)];

    /// <summary>When set, every read throws it: a failure that is neither absence nor a refusal.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Refuses every read of <paramref name="key"/>, whether it is there or not.</summary>
    public void Refuse(string key) => refused[key] = true;

    public async Task SeedAsync(string key, string body)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        if (!await inner.PutAsync(key, stream, ifMatch: null, CancellationToken.None))
        {
            throw new InvalidOperationException($"{key} was seeded twice.");
        }
    }

    public async Task<string> ETagOfAsync(string key) =>
        (await inner.GetAsync(key, ifNoneMatch: null, CancellationToken.None))?.ETag
        ?? throw new InvalidOperationException($"{key} was never seeded.");

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        reads.Enqueue((key, ifNoneMatch));

        if (FailWith is { } failure)
        {
            return Task.FromException<StoredObject?>(failure);
        }

        return refused.ContainsKey(key)
            ? Task.FromException<StoredObject?>(new ObjectReadRefusedException(key, innerException: null))
            : inner.GetAsync(key, ifNoneMatch, ct);
    }

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        throw new InvalidOperationException("The gateway cannot write (contracts/s3-layout.md, P-03).");
}
