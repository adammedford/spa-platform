using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="AccessTokenResult.Token"/> carries an access token, which is on the never-logged list
/// (N-16; <c>contracts/telemetry.md</c> -> Never logged). A positional record's generated
/// <c>ToString()</c> prints every public member, so a log call or an exception message that formats
/// the result would carry the token (change request #600).
/// </summary>
public sealed class AccessTokenResultTests
{
    [Fact]
    public void A_token_result_prints_no_token()
    {
        // Mutation: remove the ToString override, and the generated "Token { Value = ... }" fails it.
        const string value = "canary-access-token-600";
        AccessTokenResult result = new AccessTokenResult.Token(value);

        Assert.DoesNotContain(value, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, $"{result}", StringComparison.Ordinal);
        Assert.Equal(nameof(AccessTokenResult.Token), result.ToString());
    }

    [Fact]
    public void A_token_result_still_carries_its_value_and_its_equality()
    {
        // The override changes no frozen member: construction, Value, deconstruction and equality
        // are as contracts-v1 froze them.
        AccessTokenResult.Token token = new("canary-access-token-600");

        token.Deconstruct(out string value);

        Assert.Equal("canary-access-token-600", token.Value);
        Assert.Equal("canary-access-token-600", value);
        Assert.Equal(new AccessTokenResult.Token("canary-access-token-600"), token);
        Assert.NotEqual(new AccessTokenResult.Token("another-token"), token);
    }
}
