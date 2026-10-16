using System.Text.RegularExpressions;

namespace Gateway.E2ETests.Stack;

/// <summary>What a token looks like to a search through what the browser holds (F-05, N-23).</summary>
public static partial class TokenShape
{
    /// <summary>
    /// A JWT: three base64url segments, the first starting <c>eyJ</c> (an encoded <c>{"</c>). The
    /// provider's access and ID tokens are JWTs, so one from any source is caught by its shape as well as
    /// by value.
    /// </summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    public static partial Regex Jwt();

    /// <summary>Every token value the provider issued in this run, access and refresh, which the browser must hold none of.</summary>
    public static IEnumerable<string> IssuedBy(TestIdp.TestIdentityProvider idp) =>
        idp.IssuedAccessTokens.Concat(idp.IssuedRefreshTokens);
}
