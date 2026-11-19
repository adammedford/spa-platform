namespace Platform.Abstractions;

/// <summary>
/// The authentication scheme names: one pair per app, and one OpenID Connect scheme per fragment.
/// Source: docs/plan/02-contracts.md -> Interfaces. Assigned to G1b (ADR-0017); task I-6 records the
/// freeze with <c>contracts-v1</c>. Nothing in <c>src/Platform.Identity</c> starts before that tag.
/// The fragment form is the fragments spec's (#961): a fragment's scheme is
/// <c>oidc:{appId}:{fragmentId}</c> and signs in to the shell's <c>cookie:{appId}</c>, so a shell and
/// its fragments share one cookie scheme and no fragment has a cookie scheme of its own.
///
/// A cookie handler derives its data-protection purpose from its scheme name, and the OpenID Connect
/// handler its state, nonce and ticket purposes, so these strings outlive a deployment: changing one
/// invalidates every session cookie or login in flight already issued under it. Neither an
/// <c>appId</c> nor a <c>fragmentId</c> can contain <c>:</c> (both match <c>^[a-z0-9-]+$</c>), which
/// is what makes <see cref="ParseOidc"/> unambiguous.
/// </summary>
public static class SchemeNames
{
    public static string Oidc(string appId) => "oidc:" + appId;

    /// <summary>A fragment's OpenID Connect scheme: <c>oidc:{appId}:{fragmentId}</c>.</summary>
    public static string Oidc(string appId, string fragmentId) => "oidc:" + appId + ":" + fragmentId;

    public static string Cookie(string appId) => "cookie:" + appId;

    /// <summary>
    /// The app and, for a fragment's scheme, the fragment an OpenID Connect scheme name names: null
    /// for a name that does not start <c>oidc:</c>, has an empty part, or has more than two parts
    /// after the prefix. <c>oidc:app1</c> is <c>("app1", null)</c>; <c>oidc:app1:orders-ui</c> is
    /// <c>("app1", "orders-ui")</c>. It checks the shape only, not that either id is in the registry.
    /// </summary>
    public static (string AppId, string? FragmentId)? ParseOidc(string scheme)
    {
        const string Prefix = "oidc:";
        if (scheme is null || !scheme.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = scheme[Prefix.Length..].Split(':');
        if (parts.Length > 2 || Array.Exists(parts, string.IsNullOrEmpty))
        {
            return null;
        }

        return (parts[0], parts.Length == 2 ? parts[1] : null);
    }
}
