using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// <see cref="PlatformPolicy"/> from <c>Policy:*</c> and <c>Issuers:*</c> (contracts/platform-config.schema.json).
/// Read by hand rather than bound, because the binder fills neither <see cref="ImmutableArray{T}"/> nor
/// a positional record.
///
/// Fails closed: a missing list allows nothing, and an issuer missing a field, or with a
/// <c>BrowserOrigin</c> that is not an absolute URI, is not an issuer, so an entry naming it is rejected
/// with <see cref="RegistryRules.UnknownIssuer"/>. The task environment is checked against the schema
/// before it reaches a gateway (task 1.4); this is the gateway's own floor.
/// </summary>
internal static class PlatformPolicyConfiguration
{
    public static PlatformPolicy Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new PlatformPolicy(
            AllowedHostSuffixes: Strings(configuration.GetSection("Policy:AllowedHostSuffixes")),
            AllowedUpstreams: Strings(configuration.GetSection("Policy:AllowedUpstreams")),
            Issuers: configuration.GetSection("Issuers").GetChildren()
                .Select(section => (section.Key, Issuer: Issuer(section)))
                .Where(issuer => issuer.Issuer is not null)
                .ToImmutableDictionary(issuer => issuer.Key, issuer => issuer.Issuer!, StringComparer.Ordinal));
    }

    // GetChildren orders array indexes numerically, so the list keeps the configuration's order.
    private static ImmutableArray<string> Strings(IConfigurationSection section) =>
        [.. section.GetChildren().Select(child => child.Value).OfType<string>().Where(value => value.Length > 0)];

    private static IssuerDefinition? Issuer(IConfigurationSection section)
    {
        string? authority = section["Authority"];
        string? profile = section["Profile"];
        return string.IsNullOrEmpty(authority)
            || string.IsNullOrEmpty(profile)
            || !Uri.TryCreate(section["BrowserOrigin"], UriKind.Absolute, out Uri? browserOrigin)
            ? null
            : new IssuerDefinition(authority, profile, browserOrigin);
    }
}
