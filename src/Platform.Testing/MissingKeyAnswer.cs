using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// How an <see cref="IObjectStore"/> answers a read of a key that was never written: the two modes
/// <see cref="ObjectStoreContract"/> runs in, and the two <see cref="InMemoryObjectStore"/> can take.
/// Change request contracts/changes/issue-381-object-read-refusal.md, item 2.
/// </summary>
public enum MissingKeyAnswer
{
    /// <summary>Null: the store confirmed the key does not exist.</summary>
    ConfirmedAbsence,

    /// <summary>
    /// <see cref="ObjectReadRefusedException"/>: the store refused the read, as S3 refuses a missing
    /// key to a role with <c>s3:GetObject</c> and no <c>s3:ListBucket</c>, the gateway's deployed role.
    /// </summary>
    AmbiguousRefusal,
}
