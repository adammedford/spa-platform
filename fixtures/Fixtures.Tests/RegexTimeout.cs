namespace Fixtures.Tests;

/// <summary>
/// The match timeout every regex in this suite carries (MA0009, ADR-0035 batch 1, #618). The
/// inputs are the repository's own fixtures and contracts, so a match that runs a second is a
/// pattern gone wrong, and the timeout turns it into a failed test instead of a hung run.
/// </summary>
internal static class RegexTimeout
{
    /// <summary>The timeout as <c>[GeneratedRegex]</c> takes it, which needs a constant.</summary>
    internal const int Milliseconds = 1000;

    /// <summary>The same timeout for the static <c>Regex</c> methods.</summary>
    internal static readonly TimeSpan Value = TimeSpan.FromMilliseconds(Milliseconds);
}
