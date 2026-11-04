using System.Text.RegularExpressions;

namespace Platform.Telemetry.Tests;

/// <summary>
/// Redaction's fail-closed branches: a pattern that runs out of its match timeout leaves nothing of
/// the text it was cleaning (#677). The shipped patterns are linear, so no text reaches their 100 ms
/// budget on its own, and a slow value reaches Render's only at a moment no test can pin; each test
/// hands the redaction a pattern that runs out, through the overload that takes it.
/// </summary>
public sealed class RedactionTests
{
    // The catastrophic-backtracking example from Microsoft's guidance on match timeouts
    // (https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex#use-time-out-values):
    // nested quantifiers against a run of word characters that ends in a mismatch. The pattern is
    // anchored, so the run opens each text; with a 1 ms budget it throws RegexMatchTimeoutException
    // on every machine.
    private static Regex Backtracking() =>
        new(@"^(\w+\s?)*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1));

    private static readonly string Runaway = new string('a', 40) + "!";

    [Fact]
    public void A_query_string_pattern_that_times_out_leaves_nothing_of_the_text()
    {
        string text = Runaway + " /signin-oidc?code=CANARY-CODE";

        Assert.Equal(Redaction.Redacted, Redaction.WithoutQueryStrings(text, Backtracking()));
    }

    [Fact]
    public void A_sensitive_line_whose_template_cannot_be_rendered_again_carries_no_message()
    {
        List<KeyValuePair<string, object?>> properties =
        [
            new("AccessToken", "CANARY-ACCESS-TOKEN"),
            new("{OriginalFormat}", Runaway + " refreshed {AccessToken}"),
        ];

        Assert.Equal(Redaction.Redacted, Redaction.Render(properties, Backtracking()));
    }
}
