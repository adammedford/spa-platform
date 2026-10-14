using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// Whether a shell and its scripts fit a content security policy, decided from the markup and the
/// script text alone. It is deliberately narrower than a browser: it knows the handful of things a
/// fixture SPA could do that the baseline refuses (inline script, inline style, event-handler
/// attributes, eval, a scheme or an origin the policy does not list) and reports each.
///
/// The policy itself is read, not restated: the baseline is the block under "The baseline content
/// security policy" in contracts/http-contract.md, and an app's additions are its definition's
/// <c>csp</c>, which "adds sources to these ten directives and to no others".
/// </summary>
internal static partial class ContentSecurityPolicyFit
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Baseline { get; } = ReadBaseline();

    /// <summary>The baseline plus whatever the app's definition adds.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> For(JsonNode definition)
    {
        Dictionary<string, IReadOnlyList<string>> policy = new(Baseline, StringComparer.Ordinal);
        if (definition["csp"] is JsonObject additions)
        {
            foreach ((string directive, JsonNode? sources) in additions)
            {
                policy[directive] = [.. policy[directive], .. sources!.AsArray().Select(s => s!.GetValue<string>())];
            }
        }

        return policy;
    }

    /// <summary>Each thing in the shell or its scripts that the policy would refuse, described.</summary>
    internal static IReadOnlyList<string> Violations(
        string html, string scripts, IReadOnlyDictionary<string, IReadOnlyList<string>> policy)
    {
        List<string> violations = [];
        string markup = Comment().Replace(html, string.Empty);

        if (InlineScript().IsMatch(markup) && !Allows(policy, "script-src", "'unsafe-inline'"))
        {
            violations.Add("script-src: an inline <script>");
        }

        if (EventHandler().IsMatch(markup) && !Allows(policy, "script-src", "'unsafe-inline'"))
        {
            violations.Add("script-src: an inline event-handler attribute");
        }

        if ((StyleElement().IsMatch(markup) || StyleAttribute().IsMatch(markup)) && !Allows(policy, "style-src", "'unsafe-inline'"))
        {
            violations.Add("style-src: inline style");
        }

        foreach ((string directive, string source) in References(markup))
        {
            if (!Fits(policy, directive, source))
            {
                violations.Add($"{directive}: {Shorten(source)}");
            }
        }

        if (Eval().IsMatch(scripts) && !Allows(policy, "script-src", "'unsafe-eval'"))
        {
            violations.Add("script-src: eval or the Function constructor");
        }

        foreach (Match literal in AbsoluteUrlLiteral().Matches(scripts))
        {
            if (!Fits(policy, "connect-src", literal.Groups[1].Value))
            {
                violations.Add($"connect-src: {Shorten(literal.Groups[1].Value)}");
            }
        }

        return violations;
    }

    private static IEnumerable<(string Directive, string Source)> References(string markup)
    {
        foreach (Match match in Reference().Matches(markup))
        {
            string directive = match.Groups["tag"].Value.ToLowerInvariant() switch
            {
                "script" => "script-src",
                "img" => "img-src",
                "link" => "style-src",
                "form" => "form-action",
                _ => "default-src",
            };

            yield return (directive, match.Groups["url"].Value);
        }
    }

    private static bool Fits(IReadOnlyDictionary<string, IReadOnlyList<string>> policy, string directive, string source)
    {
        int colon = source.IndexOf(':', StringComparison.Ordinal);
        int slash = source.IndexOf('/', StringComparison.Ordinal);
        bool hasScheme = colon > 0 && (slash < 0 || colon < slash);

        if (!hasScheme && !source.StartsWith("//", StringComparison.Ordinal))
        {
            // A relative or root-relative URL is the page's own origin.
            return Allows(policy, directive, "'self'");
        }

        if (hasScheme && !source.AsSpan(colon + 1).StartsWith("//", StringComparison.Ordinal))
        {
            // data:, blob: and the like match only a scheme source.
            return Allows(policy, directive, source[..(colon + 1)]);
        }

        // Another origin would have to be listed by name. No fixture needs one, so none is matched.
        return false;
    }

    private static bool Allows(IReadOnlyDictionary<string, IReadOnlyList<string>> policy, string directive, string source)
    {
        IReadOnlyList<string> sources = policy.TryGetValue(directive, out IReadOnlyList<string>? listed)
            ? listed
            : policy["default-src"];

        return sources.Contains(source, StringComparer.Ordinal);
    }

    private static string Shorten(string source) => source.Length <= 40 ? source : source[..40] + "...";

    private static Dictionary<string, IReadOnlyList<string>> ReadBaseline()
    {
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));
        Match block = BaselineBlock().Match(contract);
        if (!block.Success)
        {
            throw new InvalidOperationException("contracts/http-contract.md has no baseline policy block.");
        }

        // The block names the provider's origin as a placeholder in form-action; it is the gateway's
        // to fill in, and no fixture posts there directly, so it is dropped rather than guessed.
        string text = Placeholder().Replace(block.Groups[1].Value, string.Empty);

        Dictionary<string, IReadOnlyList<string>> baseline = new(StringComparer.Ordinal);
        foreach (string directive in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = directive.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            baseline.Add(parts[0], parts[1..]);
        }

        return baseline;
    }

    [GeneratedRegex("### The baseline content security policy\\s+```text\\s*\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex BaselineBlock();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Placeholder();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<script\\b(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<[a-z][^>]*\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandler();

    [GeneratedRegex("<style\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElement();

    [GeneratedRegex("<[a-z][^>]*\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<(?<tag>script|img|link|form|iframe|object|embed|source|video|audio)\\b[^>]*\\s(?:src|href|action|data)=\"(?<url>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    [GeneratedRegex("\\beval\\s*\\(|\\bnew\\s+Function\\s*\\(|\\bFunction\\s*\\(")]
    private static partial Regex Eval();

    [GeneratedRegex("[\"'`]([a-z][a-z0-9+.-]*:(?://)?[^\"'`\\s]*)[\"'`]", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlLiteral();
}
