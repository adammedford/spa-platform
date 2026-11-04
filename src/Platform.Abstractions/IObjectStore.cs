namespace Platform.Abstractions;

/// <summary>
/// The object store behind the registry and every release: S3 in the gateway and in <c>spactl</c>,
/// memory in tests. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Reads a key. Null means the store confirmed the key does not exist. When
    /// <paramref name="ifNoneMatch"/> is the object's current ETag the result is
    /// <see cref="StoredObject.NotModified"/> with no body, which is how the registry source polls
    /// without paying for a body it already has.
    /// </summary>
    /// <exception cref="ObjectReadRefusedException">
    /// The store refused the read, which is ambiguous: a missing key under a role without listing,
    /// or an existing key the role may not read. The caller decides what that means for its key, and
    /// never reads it as null. Change request contracts/changes/issue-381-object-read-refusal.md,
    /// item 1.
    /// </exception>
    Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct);

    /// <summary>
    /// Writes only if the key is absent (<paramref name="ifMatch"/> null) or still has that ETag.
    /// False means it did not write. Only <c>spactl</c> calls this: the gateway's task role cannot
    /// write to the bucket.
    /// </summary>
    Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct);
}

/// <summary>
/// One object read from the store. <see cref="Body"/> is null when <see cref="NotModified"/> is
/// true. The caller owns the stream and disposes it.
/// </summary>
public sealed record StoredObject(Stream? Body, string ETag, long? Length, bool NotModified);
