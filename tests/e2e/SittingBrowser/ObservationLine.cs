using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// One observation line (GLOSSARY.md -> "Observation line"), as #1024 Q3 shapes it: the token first,
/// the UTC time to the second, the profile, then <c>key=value</c> fields, never JSON, and the one free
/// text last. A key is never one <c>tools/wiz_redact.py</c> elides (<see cref="ElidedKeys"/>), so the
/// redactor passes every line unchanged and is a second guard behind this grammar, not the first.
/// </summary>
public static partial class ObservationLine
{
    /// <summary>The keys the redactor's rules 3, 4 and 8 elide the value of; no field is named one of them.</summary>
    public static readonly IReadOnlyList<string> ElidedKeys = ["state", "code", "id", "value", "client_id"];

    /// <summary>The profile column of a line about the process rather than one profile.</summary>
    public const string NoProfile = "-";

    /// <summary>A field with nothing to say: a <c>/readyz</c> probe that failed or answered 503.</summary>
    public const string Dash = "-";

    /// <summary>The width the token is padded to, as #1024's examples align it.</summary>
    private const int TokenWidth = 13;

    /// <summary>
    /// The line. A field value carries no whitespace; the free text, when given, has its whitespace
    /// folded to single spaces. A key the redactor elides is a programming error, refused here.
    /// </summary>
    public static string Format(string token, DateTimeOffset at, string profile, IEnumerable<(string Key, string Value)> fields, (string Key, string Text)? free = null)
    {
        StringBuilder line = new();
        line.Append(token.PadRight(TokenWidth)).Append(' ').Append(Time(at)).Append(' ').Append(profile);
        foreach ((string key, string value) in fields)
        {
            Field(line, key, value);
        }

        if (free is ({ } freeKey, { } text))
        {
            CheckKey(freeKey);
            line.Append(' ').Append(freeKey).Append('=').Append(Whitespace().Replace(text.Trim(), " "));
        }

        return line.ToString();
    }

    /// <summary>The UTC time to the second, as every line carries it.</summary>
    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A line's token, time and profile, as a parser in the wiz skill reads them; null for a line that is not one.</summary>
    public static (string Token, string Time, string Profile)? Head(string line) =>
        HeadPattern().Match(line) is { Success: true } head ? (head.Groups[1].Value, head.Groups[2].Value, head.Groups[3].Value) : null;

    private static void Field(StringBuilder line, string key, string value)
    {
        CheckKey(key);
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"the field {key}'s value is empty or has whitespace; only the free text may.", nameof(value));
        }

        line.Append(' ').Append(key).Append('=').Append(value);
    }

    private static void CheckKey(string key)
    {
        if (ElidedKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"{key} is not a key an observation line may carry.", nameof(key));
        }
    }

    [GeneratedRegex("^[a-z][a-z-]*$", RegexOptions.None, 1000)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^([a-z][a-z-]*) +(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z) (\S+)(?: |$)", RegexOptions.None, 1000)]
    private static partial Regex HeadPattern();
}
