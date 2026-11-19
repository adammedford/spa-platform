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

    [Fact]
    public void A_fragment_scheme_is_the_appId_and_fragmentId_behind_an_oidc_prefix()
    {
        // The fragments spec (#961): the literal is the specification, as for the per-app pair.
        Assert.Equal("oidc:app1:orders-ui", SchemeNames.Oidc("app1", "orders-ui"));
    }

    [Fact]
    public void A_fragment_never_shares_a_scheme_name_with_its_shell_or_another_fragment()
    {
        string fragment = SchemeNames.Oidc("app1", "orders-ui");

        Assert.NotEqual(SchemeNames.Oidc("app1"), fragment);
        Assert.NotEqual(SchemeNames.Cookie("app1"), fragment);
        Assert.NotEqual(SchemeNames.Oidc("app1", "billing-ui"), fragment);
        Assert.NotEqual(SchemeNames.Oidc("app2", "orders-ui"), fragment);
    }

    [Fact]
    public void ParseOidc_round_trips_the_shell_form_and_the_fragment_form()
    {
        // Mutation: return (parts[0], null) for every name, or swap the two parts.
        Assert.Equal(("app1", (string?)null), SchemeNames.ParseOidc(SchemeNames.Oidc("app1")));
        Assert.Equal(("app1", (string?)"orders-ui"), SchemeNames.ParseOidc(SchemeNames.Oidc("app1", "orders-ui")));
    }

    [Theory]
    [InlineData("cookie:app1")]
    [InlineData("oidc:")]
    [InlineData("oidc:app1:")]
    [InlineData("oidc::orders-ui")]
    [InlineData("oidc:a:b:c")]
    [InlineData("OIDC:app1")]
    [InlineData("app1")]
    public void ParseOidc_is_null_for_a_name_that_is_not_an_oidc_scheme_of_either_shape(string scheme)
    {
        // Mutation: drop the empty-part check, or allow more than two parts.
        Assert.Null(SchemeNames.ParseOidc(scheme));
    }
}
