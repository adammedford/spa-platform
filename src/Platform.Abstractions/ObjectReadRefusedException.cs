namespace Platform.Abstractions;

/// <summary>
/// The store refused a read: <see cref="IObjectStore.GetAsync"/> could not tell whether the key is
/// absent. Null from that method means the store confirmed the key does not exist; this exception
/// means it did not, and the caller decides what that means for its key.
///
/// <para>The gateway's task role grants <c>s3:GetObject</c> and no <c>s3:ListBucket</c> (P-03), and
/// without <c>ListBucket</c> S3 answers a missing key with 403 rather than 404
/// (https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html, "Permissions"). A 403 is then
/// either a missing key or an existing key the role may not read, and only the caller knows which
/// answer its key deserves: the registry source fails the refresh, the asset route answers by
/// whether the key is the shell, and <c>spactl</c> stops with a diagnostic. None of them reads it as
/// "missing".</para>
///
/// <para>A store wraps its own exception as <see cref="Exception.InnerException"/>; a store with none,
/// as the in-memory double's refusal mode has none, passes null. It derives from
/// <see cref="Exception"/> directly so that catching it catches no transport failure and no
/// cancellation, which propagate unchanged.</para>
///
/// <para>Source: change request contracts/changes/issue-381-object-read-refusal.md, item 1, decided
/// 26 October 2026 on #381. A new type that changes no stage, owner or entry point's signature
/// (contracts/README.md -> "What may land between gates").</para>
/// </summary>
public sealed class ObjectReadRefusedException : Exception
{
    /// <summary>A refusal of the read of <paramref name="key"/>.</summary>
    /// <param name="key">The key whose read the store refused. A bucket path, never a credential.</param>
    /// <param name="innerException">The store's own exception, or null when it has none.</param>
    public ObjectReadRefusedException(string key, Exception? innerException)
        : base(MessageFor(key), innerException)
    {
        Key = key;
    }

    /// <summary>The key whose read the store refused.</summary>
    public string Key { get; }

    private static string MessageFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return $"The store refused the read of '{key}': the key is missing or not readable.";
    }
}
