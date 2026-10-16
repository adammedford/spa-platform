namespace Platform.Abstractions;

/// <summary>
/// The authentication scheme names, one pair per app. Source: docs/plan/02-contracts.md ->
/// Interfaces. Assigned to G1b (ADR-0017); task I-6 records the freeze with
/// <c>contracts-v1</c>. Nothing in <c>src/Platform.Identity</c> starts before that tag.
///
/// A cookie handler derives its data-protection purpose from its scheme name, so these strings
/// outlive a deployment: changing one invalidates every session cookie already issued under it.
/// </summary>
public static class SchemeNames
{
    public static string Oidc(string appId) => "oidc:" + appId;

    public static string Cookie(string appId) => "cookie:" + appId;
}
