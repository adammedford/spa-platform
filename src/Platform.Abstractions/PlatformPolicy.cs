using System.Collections.Immutable;

namespace Platform.Abstractions;

/// <summary>
/// The policy-bounded parts of platform configuration, as the registry validator and the options
/// setup read them. Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a.
///
/// These are the bounds a JSON schema cannot express, because the schema cannot see configuration:
/// an entry's <c>host</c> must end with an allowed suffix, each <c>apis[].upstream</c> must match
/// the allowed upstreams, and <c>oauth.issuer</c> must name one of <see cref="Issuers"/>. The one
/// validation library of task 2.1 checks all three, so the gateway and <c>spactl</c> cannot
/// disagree.
/// </summary>
public sealed record PlatformPolicy(
    ImmutableArray<string> AllowedHostSuffixes,
    ImmutableArray<string> AllowedUpstreams,
    ImmutableDictionary<string, IssuerDefinition> Issuers);
