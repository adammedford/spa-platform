using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// Assigned to G1b (ADR-0017); the contracts-v1 tag records the freeze. These are the
/// names docs/plan/02-contracts.md fixes, and the expected values below are copied from it.
///
/// The strings matter beyond naming. A cookie handler derives its data-protection purpose from its
/// scheme name, so changing one of these silently invalidates every session cookie already issued,
/// and two apps sharing a name would share a protector. The literals are the specification.
/// </summary>
public sealed class SchemeNamesTests
{
    [Fact]
    public void The_oidc_scheme_is_the_appId_behind_an_oidc_prefix()
    {
        Assert.Equal("oidc:app1", SchemeNames.Oidc("app1"));
    }

    [Fact]
    public void The_cookie_scheme_is_the_appId_behind_a_cookie_prefix()
    {
        Assert.Equal("cookie:app1", SchemeNames.Cookie("app1"));
    }

    [Fact]
    public void Two_apps_never_share_a_scheme_name()
    {
        Assert.NotEqual(SchemeNames.Oidc("app1"), SchemeNames.Oidc("app2"));
        Assert.NotEqual(SchemeNames.Cookie("app1"), SchemeNames.Cookie("app2"));
    }

    [Fact]
    public void One_app_never_shares_a_name_between_its_two_schemes()
    {
        Assert.NotEqual(SchemeNames.Oidc("app1"), SchemeNames.Cookie("app1"));
    }
}
