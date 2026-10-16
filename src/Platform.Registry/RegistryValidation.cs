using System.Collections.Frozen;
using System.Collections.Immutable;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// What <see cref="RegistryValidator.Validate"/> found. Errors and warnings are kept apart (plan row
/// 2.1): any error rejects the document whole (ADR-0002), and a warning never does.
/// </summary>
/// <param name="Revision">The document's revision, or 0 when it could not be read.</param>
/// <param name="Apps">
/// Every entry, modelled, in document order. Empty whenever <see cref="Errors"/> is not, so a caller
/// cannot apply part of a rejected document (S-D8, N-14).
/// </param>
public sealed record RegistryValidation(
    long Revision,
    ImmutableArray<AppDefinition> Apps,
    ImmutableArray<RegistryIssue> Errors,
    ImmutableArray<RegistryIssue> Warnings)
{
    public bool IsValid => Errors.IsEmpty;

    /// <summary>
    /// The snapshot this document becomes, indexed by host and by appId. The ETag is the S3 object's,
    /// which only the registry source of task 2.2 knows. Throws for a rejected document.
    /// </summary>
    public RegistrySnapshot ToSnapshot(string etag)
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"A rejected registry document has no snapshot ({Errors.Length} error(s), the first: {Errors[0]}).");
        }

        // Ordinal: the schema admits only lowercase hosts and appIds, and host resolution matches
        // the request authority exactly (contracts/pipeline.md, UsePlatformHostResolution).
        return new RegistrySnapshot(
            Revision,
            etag,
            Apps.ToFrozenDictionary(app => app.Host, StringComparer.Ordinal),
            Apps.ToFrozenDictionary(app => app.AppId, StringComparer.Ordinal));
    }
}

/// <summary>What <see cref="RegistryValidator.ValidateAppDefinition"/> found, kept apart as above.</summary>
public sealed record AppDefinitionValidation(
    ImmutableArray<RegistryIssue> Errors,
    ImmutableArray<RegistryIssue> Warnings)
{
    public bool IsValid => Errors.IsEmpty;
}
