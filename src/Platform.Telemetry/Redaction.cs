using System.Text.RegularExpressions;

namespace Platform.Telemetry;

/// <summary>
/// What the formatter removes from a line that reached it anyway. The loggers that write a
/// credential are held quiet by <see cref="LogFloors"/>; this is the second line, for a logger
/// nobody listed and a property somebody named plainly.
/// </summary>
internal static partial class Redaction
{
    public const string Redacted = "[redacted]";

    // contracts/telemetry.md -> Never logged. Matched against a property name with the separators
    // taken out and lower-cased, so AccessToken, access_token and access-token are one name.
    private static readonly string[] SensitiveFragments =
        ["token", "secret", "password", "cookie", "authorization", "sessionkey", "querystring", "assertion"];

    private static readonly HashSet<string> SensitiveNames =
        new(["code", "state", "query", "authcode", "nonce", "codeverifier"], StringComparer.Ordinal);

    /// <summary>True when a property of this name must not carry its value into a line.</summary>
    public static bool IsSensitiveName(string name)
    {
        string flat = Flatten(name);
        return SensitiveNames.Contains(flat) || SensitiveFragments.Any(f => flat.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>
    /// The text with the query string cut from every URL or path in it. The callback carries the
    /// authorization code and state in its query (ADR-0007), so no query survives, whatever it holds.
    /// </summary>
    public static string WithoutQueryStrings(string text) =>
        text.Contains('?', StringComparison.Ordinal) ? QueryString().Replace(text, "?" + Redacted) : text;

    /// <summary>
    /// The message rendered again from its template with each sensitive hole redacted, or null when
    /// no property is sensitive and the logger's own rendering can stand. The logger's rendering
    /// substitutes every value, so a credential in a plainly named property would be in the message
    /// even after its field was redacted.
    /// </summary>
    public static string? Render(IReadOnlyList<KeyValuePair<string, object?>>? properties)
    {
        if (properties is null)
        {
            return null;
        }

        string? template = null;
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        bool sensitive = false;
        foreach (KeyValuePair<string, object?> property in properties)
        {
            if (property.Key == "{OriginalFormat}")
            {
                template = property.Value as string;
            }
            else
            {
                values[property.Key] = property.Value;
                sensitive |= IsSensitiveName(property.Key);
            }
        }

        if (!sensitive)
        {
            return null;
        }

        // With no template to render from, the safe message is none at all.
        if (template is null)
        {
            return Redacted;
        }

        return Hole().Replace(template, match =>
        {
            if (match.Value is "{{" or "}}")
            {
                return match.Value[..1];
            }

            string name = match.Groups["name"].Value;
            if (IsSensitiveName(name))
            {
                return Redacted;
            }

            return values.TryGetValue(name, out object? value)
                ? System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(null)"
                : match.Value;
        });
    }

    private static string Flatten(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (c is not ('_' or '-' or '.' or ' '))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    // A '?' directly after a non-space character, followed by at least one character that could be
    // part of a URL. "/signin-oidc?code=x" and "https://h/p?a=b" match; "is it? yes" does not.
    // A message-template hole, {Name}, {@Name} or {Name:format}, or an escaped brace.
    [GeneratedRegex(@"\{\{|\}\}|\{[@$]?(?<name>[^{}:,]+)(?:[,:][^{}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Hole();

    [GeneratedRegex(@"(?<=\S)\?[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryString();
}
