using System.Collections.Immutable;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// Registry entries for tests. <see cref="Entry"/> builds a registry entry: an app definition plus
/// the client id, the client secret reference and the release; <see cref="Fragment"/> builds one of its
/// fragments. Every workstream needs one, and none of them should spell out eight constructor
/// arguments to get it.
///
/// The defaults build the smallest registry entry the record accepts, not one platform policy would
/// necessarily admit, and not a copy of any fixture: no upstream, no content security policy additions,
/// no fragments, an eight-hour session. Pass only what the test cares about. Real app definitions live
/// under <c>fixtures/apps/</c>, and fragment ones under <c>fixtures/fragments/</c> once the fragments
/// spec's fixtures land them.
/// </summary>
public static class TestApps
{
    public const string HostSuffix = ".spa-poc.test";

    public static AppDefinition Entry(
        string appId = "app1",
        string? host = null,
        string release = "2026-10-12-abc1234",
        string issuer = "primary",
        string? clientId = null,
        string? clientSecretRef = null,
        IEnumerable<string>? scopes = null,
        string? resource = null,
        IEnumerable<ApiDefinition>? apis = null,
        ImmutableDictionary<string, ImmutableArray<string>>? csp = null,
        TimeSpan? sessionLifetime = null,
        string? fingerprint = null,
        IEnumerable<FragmentDefinition>? fragments = null) =>
        new(
            AppId: appId,
            Host: host ?? appId + HostSuffix,
            Release: release,
            OAuth: new OAuthDefinition(
                Issuer: issuer,
                ClientId: clientId ?? "client-" + appId,
                ClientSecretRef: clientSecretRef ?? $"ssm:/spa-platform/poc/apps/{appId}/client-secret:1",
                Scopes: scopes is null ? ["openid", "profile"] : [.. scopes],
                Resource: resource),
            Apis: apis is null ? [] : [.. apis],
            Csp: csp ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty,
            SessionLifetime: sessionLifetime ?? TimeSpan.FromHours(8),
            // Distinct per app by default, because the fingerprint is what decides eviction.
            Fingerprint: fingerprint ?? "sha256:" + appId + "-" + release)
        {
            Fragments = fragments is null ? [] : [.. fragments],
        };

    /// <summary>
    /// One fragment of the entry <paramref name="appId"/> names. The client id defaults to
    /// <c>client-{appId}-{fragmentId}</c>, distinct from every shell's and every other fragment's, as
    /// the validator requires; the secret reference to the apps root's parameter for the fragment.
    /// A fingerprint is the entry's, so a test that changes a fragment passes a new one to
    /// <see cref="Entry"/>.
    /// </summary>
    public static FragmentDefinition Fragment(
        string fragmentId = "frag1",
        string appId = "app1",
        string release = "2026-11-04-def5678",
        string issuer = "primary",
        string? clientId = null,
        string? clientSecretRef = null,
        IEnumerable<string>? scopes = null,
        string? resource = null,
        IEnumerable<ApiDefinition>? apis = null,
        ImmutableDictionary<string, ImmutableArray<string>>? csp = null,
        string entry = PlatformPaths.DefaultEntry) =>
        new(
            FragmentId: fragmentId,
            OAuth: new OAuthDefinition(
                Issuer: issuer,
                ClientId: clientId ?? $"client-{appId}-{fragmentId}",
                ClientSecretRef: clientSecretRef
                    ?? $"ssm:/spa-platform/poc/apps/{appId}/fragments/{fragmentId}/client-secret:1",
                Scopes: scopes is null ? ["openid", "profile"] : [.. scopes],
                Resource: resource),
            Release: release,
            Apis: apis is null ? [] : [.. apis],
            Csp: csp ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty,
            Entry: entry);

    /// <summary>
    /// One upstream API, reached at <c>/api/{name}/**</c> on a shell's entry and at
    /// <c>/fragments/{fragmentId}/api/{name}/**</c> on a fragment's.
    /// </summary>
    public static ApiDefinition Api(string name = "orders", string? upstream = null, int timeoutSeconds = 30) =>
        new(name, new Uri(upstream ?? $"https://{name}.internal"), TimeSpan.FromSeconds(timeoutSeconds));
}
