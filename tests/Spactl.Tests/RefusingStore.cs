using Platform.Abstractions;

namespace Spactl.Tests;

/// <summary>
/// <see cref="VersionedStore"/> with the reads of some keys refused, as S3 refuses them to a role
/// without <c>s3:ListBucket</c> or without read on the key: <see cref="ObjectReadRefusedException"/>,
/// which the store raises in place of the SDK's 403 <c>AccessDenied</c> (change request
/// contracts/changes/issue-381-object-read-refusal.md, item 1). Writes go through, so
/// <see cref="VersionedStore.Written"/> still records every write the command made.
/// </summary>
internal sealed class RefusingStore(VersionedStore inner, params string[] refused) : IObjectStore
{
    private readonly HashSet<string> refused = new(refused, StringComparer.Ordinal);

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) =>
        refused.Contains(key)
            ? Task.FromException<StoredObject?>(new ObjectReadRefusedException(key, innerException: null))
            : inner.GetAsync(key, ifNoneMatch, ct);

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        inner.PutAsync(key, body, ifMatch, ct);
}
