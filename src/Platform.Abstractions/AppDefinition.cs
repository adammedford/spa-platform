using System.Collections.Immutable;

namespace Platform.Abstractions;

/// <summary>
/// One registry entry, validated. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a,
/// except <see cref="OAuthDefinition"/>, which the registry document assigns to G1b (ADR-0017).
/// </summary>
/// <param name="Fingerprint">
/// Hash of the entry's canonical JSON, computed by the validator in task 2.1. This decides eviction,
/// not record equality: the immutable collections above compare by reference, and every reload
/// builds new ones.
/// </param>
public sealed record AppDefinition(
    string AppId,
    string Host,
    string Release,
    OAuthDefinition OAuth,
    ImmutableArray<ApiDefinition> Apis,
    ImmutableDictionary<string, ImmutableArray<string>> Csp,
    TimeSpan SessionLifetime,
    string Fingerprint);

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

/// <summary>One upstream API, reached at <c>/api/{Name}/**</c> with the prefix removed.</summary>
public sealed record ApiDefinition(string Name, Uri Upstream, TimeSpan Timeout);
