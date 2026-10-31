namespace Gateway.TestHarness;

/// <summary>
/// The match timeout every regular expression in the composed suites carries (MA0009, a security
/// rule at error in both analysis tiers: ADR-0035 decision 4). The suites match repository files and
/// responses they made themselves, so no match comes near it; it bounds a pattern a later edit makes
/// backtrack, which then fails its test with a timeout instead of hanging the run.
/// </summary>
public static class RegexTimeout
{
    /// <summary>The timeout in milliseconds, for <c>[GeneratedRegex]</c>, whose argument must be a constant.</summary>
    public const int Milliseconds = 1000;

    /// <summary>The same timeout, for a <c>Regex</c> constructed at run time.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMilliseconds(Milliseconds);
}
