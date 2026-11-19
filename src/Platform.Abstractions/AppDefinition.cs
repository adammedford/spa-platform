using System.Collections.Immutable;

namespace Platform.Abstractions;

/// <summary>
/// One registry entry, validated. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a,
/// except <see cref="OAuthDefinition"/>, which the registry document assigns to G1b (ADR-0017).
/// </summary>
/// <param name="Fingerprint">
/// Hash of the entry's canonical JSON, computed by the validator in task 2.1. This decides eviction,
/// not record equality: the immutable collections above compare by reference, and every reload
/// builds new ones. The JSON is the whole entry, its <c>fragments</c> included, so a change to any
/// part of an entry changes its fingerprint (#961: the whole-entry fingerprint).
/// </param>
public sealed record AppDefinition(
    string AppId,
    string Host,
    string Release,
    OAuthDefinition OAuth,
    ImmutableArray<ApiDefinition> Apis,
    ImmutableDictionary<string, ImmutableArray<string>> Csp,
    TimeSpan SessionLifetime,
    string Fingerprint)
{
    /// <summary>
    /// The fragments this entry composes, in registry order, which is the order the gateway builds
    /// their schemes in, chains their logins in and merges their content security policy additions
    /// in. Empty for an entry with no <c>fragments</c>, which means what an entry meant before them.
    /// An init property rather than a ninth positional parameter, so that the constructor every
    /// workstream already calls keeps its signature (the fragments spec, #961).
    /// </summary>
    public ImmutableArray<FragmentDefinition> Fragments { get; init; } = [];
}

/// <summary>
/// The entry's OAuth client. <paramref name="Issuer"/> names an issuer in platform configuration; it
/// is not an authority URL. <paramref name="Resource"/> is null for an app with no resource binding.
/// </summary>
public sealed record OAuthDefinition(
    string Issuer,
    string ClientId,
    string ClientSecretRef,
    ImmutableArray<string> Scopes,
    string? Resource);

/// <summary>
/// One fragment of a shell's entry: one owner's unit with its own OAuth client, release, APIs and
/// content security policy additions, which exists only inside its shell's entry and has no host or
/// session of its own (the fragments spec, #961). <paramref name="FragmentId"/> is unique within its
/// shell and names the fragment's scheme, S3 prefix, path namespace and log field.
/// <paramref name="OAuth"/> is the frozen <c>$defs/oauth</c>, whose issuer equals the shell's.
/// <paramref name="Csp"/> extends only <c>script-src</c>, <c>style-src</c>, <c>img-src</c>,
/// <c>font-src</c> and <c>connect-src</c>. <paramref name="Entry"/> is the entry module's file name at
/// the release root, <c>entry.js</c> when the document names none.
/// </summary>
public sealed record FragmentDefinition(
    string FragmentId,
    OAuthDefinition OAuth,
    string Release,
    ImmutableArray<ApiDefinition> Apis,
    ImmutableDictionary<string, ImmutableArray<string>> Csp,
    string Entry);

/// <summary>
/// One upstream API, reached with the prefix removed at <c>/api/{Name}/**</c> for a shell's entry and
/// at <c>/fragments/{fragmentId}/api/{Name}/**</c> for a fragment's. A shell's names and each
/// fragment's are separate namespaces.
/// </summary>
public sealed record ApiDefinition(string Name, Uri Upstream, TimeSpan Timeout);
