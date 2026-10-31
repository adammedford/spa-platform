using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>What a token looks like to a search through what the browser holds (F-05, N-23).</summary>
public static partial class TokenShape
{
    /// <summary>
    /// A JWT: three base64url segments, the first starting <c>eyJ</c> (an encoded <c>{"</c>). The
    /// provider's access and ID tokens are JWTs, so one from any source is caught by its shape as well as
    /// by value.
    /// </summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", RegexOptions.None, RegexTimeout.Milliseconds)]
    public static partial Regex Jwt();

    /// <summary>Every token value the provider issued in this run, access and refresh, which the browser must hold none of.</summary>
    public static IEnumerable<string> IssuedBy(TestIdp.TestIdentityProvider idp) =>
        idp.IssuedAccessTokens.Concat(idp.IssuedRefreshTokens);

    /// <summary>
    /// Fails when <paramref name="text"/> holds a JWT-shaped value, saying where but never what: what it
    /// found is the token the search keeps out of the browser, and a failure message is output too
    /// (AGENTS.md). xUnit's own DoesNotMatch would print the text it searched.
    /// </summary>
    public static void AssertNoJwtIn(string text, string where)
    {
        Match found = Jwt().Match(text);
        Assert.False(found.Success, $"{where} holds a JWT-shaped value at offset {found.Index}.");
    }

    /// <summary>Fails when <paramref name="text"/> holds a token the provider issued, counting them but naming none.</summary>
    public static void AssertNoneIssuedIn(TestIdp.TestIdentityProvider idp, string text, string where)
    {
        string[] issued = [.. IssuedBy(idp)];
        Assert.True(issued.Length > 0, "the provider issued no token, so the search could not fail.");
        int found = issued.Count(token => text.Contains(token, StringComparison.Ordinal));
        Assert.True(found == 0, $"{where} holds {found} of the {issued.Length} tokens the provider issued.");
    }
}
