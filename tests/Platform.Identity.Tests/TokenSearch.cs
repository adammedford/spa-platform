using System.Text.RegularExpressions;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// The search F-05 and N-23 make of what the browser received: none of the tokens the provider issued,
/// and nothing shaped like one. The provider's access and ID tokens are JWTs, three base64url segments
/// whose first starts <c>eyJ</c> (an encoded <c>{"</c>), so a JWT from any source is caught by shape as
/// well as by value.
/// </summary>
internal static partial class TokenSearch
{
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    public static void AssertNoTokens(TestIdentityProvider idp, string text)
    {
        Assert.NotEmpty(idp.IssuedAccessTokens);
        Assert.NotEmpty(idp.IssuedRefreshTokens);
        foreach (string token in idp.IssuedAccessTokens.Concat(idp.IssuedRefreshTokens))
        {
            // By label, never by value, so a failure does not print the token (TestIdentityProvider.Label).
            Assert.False(text.Contains(token, StringComparison.Ordinal), $"found {TestIdentityProvider.Label(token)}");
        }

        Assert.False(Jwt().IsMatch(text), "found a JWT-shaped string");
    }
}
