namespace Spactl;

/// <summary>
/// The registry object's version history, which <c>spactl registry rollback</c> reads. The bucket keeps
/// every version of <c>registry/registry.json</c>, and that versioning is part of the contract
/// (contracts/s3-layout.md, last paragraph).
///
/// spactl's own seam, not a Platform.Abstractions interface: <see cref="Platform.Abstractions.IObjectStore"/>
/// has no versions, and the gateway never reads history. <see cref="S3RegistryHistory"/> is the S3
/// adapter; the tests use a versioned in-memory store. Task 6.9 proves the S3 adapter on the deployed bucket.
/// </summary>
public interface IRegistryHistory
{
    /// <summary>The key's version ids, newest first, delete markers left out. Empty when it has none.</summary>
    Task<IReadOnlyList<string>> ListVersionsAsync(string key, CancellationToken ct);

    /// <summary>One version's body. The caller disposes the stream.</summary>
    Task<Stream> OpenVersionAsync(string key, string versionId, CancellationToken ct);
}
