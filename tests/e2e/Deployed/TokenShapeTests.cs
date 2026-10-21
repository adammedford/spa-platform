using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The token search F-05 and N-23 run over what the browser holds (<see cref="TokenShape"/>): on the
/// cell what it finds is a real token, so its failure says where and never what.
/// </summary>
public sealed class TokenShapeTests
{
    private const string Canary = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJDQU5BUlkifQ.c2lnbmF0dXJl";

    [Fact]
    public void A_jwt_in_the_text_fails_the_search_and_the_message_does_not_print_it()
    {
        Exception failed = Assert.ThrowsAny<Exception>(() => TokenShape.AssertNoJwtIn($"__Host-spa-session=abc\n{{\"body\":\"{Canary}\"}}", "what the browser holds"));

        Assert.Contains("what the browser holds", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__Host-spa-session=abc", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_with_no_jwt_passes_the_search() =>
        TokenShape.AssertNoJwtIn("""{"csrfToken":"CfDJ8-not-a-jwt","user":"alice"}""", "a BFF response body");
}
