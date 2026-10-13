namespace Platform.Abstractions;

/// <summary>
/// The authentication scheme names, one pair per app. Source: docs/plan/02-contracts.md ->
/// Interfaces. <b>Draft until G1b:</b> task I-6 folds the G0 findings in and tags
/// <c>contracts-v1</c>. Nothing in <c>src/Platform.Identity</c> starts before that tag.
///
/// A cookie handler derives its data-protection purpose from its scheme name, so these strings
/// outlive a deployment: changing one invalidates every session cookie already issued under it.
/// </summary>
[Freeze("G1b")]
public static class SchemeNames
{
    public static string Oidc(string appId) => "oidc:" + appId;

    public static string Cookie(string appId) => "cookie:" + appId;
}
