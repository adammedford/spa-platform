using System.Collections.Immutable;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// Registry entries for tests. Every workstream needs an <see cref="AppDefinition"/> and none of
/// them should spell out eight constructor arguments to get one.
///
/// The defaults mirror <c>fixtures/apps/app1.json</c>: a host under the allowed suffix, one upstream,
/// no content security policy additions, an eight-hour session. Pass only what the test cares about.
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
        string? fingerprint = null) =>
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
            Fingerprint: fingerprint ?? "sha256:" + appId + "-" + release);

    /// <summary>One upstream API, reached at <c>/api/{name}/**</c>.</summary>
    public static ApiDefinition Api(string name = "orders", string? upstream = null, int timeoutSeconds = 30) =>
        new(name, new Uri(upstream ?? $"https://{name}.internal"), TimeSpan.FromSeconds(timeoutSeconds));
}
