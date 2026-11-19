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
/// disagree. The fragments spec (#961) adds two values a measurement is to replace, as init
/// properties so that the constructor keeps its signature.
/// </summary>
public sealed record PlatformPolicy(
    ImmutableArray<string> AllowedHostSuffixes,
    ImmutableArray<string> AllowedUpstreams,
    ImmutableDictionary<string, IssuerDefinition> Issuers)
{
    /// <summary>
    /// <c>Policy:FragmentsPerShell</c>: the most fragments one entry may list, which the validator
    /// refuses past. Placeholder 5: <c>/bff/login</c> to <c>returnUrl</c> takes 4 + 3N redirects on
    /// TestIdp, 19 at N = 5 and 22 at N = 6, against the browser's network error at 20. G9's sitting
    /// (O1) replaces it with Cognito's measured count.
    /// </summary>
    public int FragmentsPerShell { get; init; } = 5;

    /// <summary>
    /// <c>Policy:MergeBudget</c>: how many attempts a fragment callback's write (a chain hop, the
    /// fallback login, an <c>unavailable</c> mark) makes at its versioned replace, none started later
    /// than <c>StoreDeadline</c> after the first attempt's send. Placeholder 3, worst case 15 s; a
    /// defect until G9's sitting (O2) sets it.
    /// </summary>
    public int MergeBudget { get; init; } = 3;
}
