using System.Globalization;

namespace Platform.Registry;

/// <summary>The policy-bounded comparisons on a host and an upstream, kept in one place.</summary>
internal static class HostPolicy
{
    private const int MaximumPort = 65535;

    /// <summary>
    /// <c>host</c> ends with an allowed suffix (contracts/README.md). The suffix carries the port when
    /// hosts do (platform-config.schema.json), so <c>.spa-poc.test:8443</c> refuses a host with no port.
    /// A suffix that does not begin with a dot must still meet the host at a label boundary, or
    /// <c>spa-poc.test</c> would allow <c>evilspa-poc.test</c>.
    /// </summary>
    public static bool EndsWithSuffix(string host, string suffix)
    {
        if (suffix.Length == 0 || !host.EndsWith(suffix, StringComparison.Ordinal) || host.Length == suffix.Length)
        {
            return false;
        }

        return suffix[0] == '.' || host[host.Length - suffix.Length - 1] == '.';
    }

    /// <summary>
    /// The schema bounds a port's shape and not its value (registry.schema.json, <c>host</c>), so an
    /// authority such as <c>app1.spa-poc.test:99999</c> passes it. An IPv6 literal's colons sit inside
    /// brackets and are not a port.
    /// </summary>
    public static bool PortAboveMaximum(string authority)
    {
        int colon = authority.LastIndexOf(':');
        if (colon < 0 || colon < authority.LastIndexOf(']'))
        {
            return false;
        }

        ReadOnlySpan<char> digits = authority.AsSpan(colon + 1);
        if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        // The upstream pattern bounds no length at all, so a port too long to parse is above the maximum.
        return digits.Length > 5 || int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) > MaximumPort;
    }

    /// <summary>
    /// The origin an upstream names, normalised the way <see cref="Uri"/> normalises one: scheme and
    /// host lowercased, a default port dropped. Phase 1's <c>Policy:AllowedUpstreams</c> holds literal
    /// origins (docs/plan/02-contracts.md, Platform configuration), so the upstream rule is equality of
    /// origins; "Phase 2 uses https patterns only" names a later shape, not this one. Null when the text
    /// is not an absolute http or https URI.
    /// </summary>
    public static string? Origin(string upstream) =>
        Uri.TryCreate(upstream, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
