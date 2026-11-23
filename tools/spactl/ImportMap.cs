using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Spactl;

/// <summary>
/// The import-map half of the registry write check (the fragments spec, #961 → "The import map", and
/// its traces comment's C9 scanner). The HTML standard makes an import map inline-only and runs the
/// content security policy's inline check on it, which never consults <c>'self'</c>, so a shell's map
/// loads only when a hash of its exact text is a <c>script-src</c> source the gateway sends
/// (https://html.spec.whatwg.org/multipage/webappapis.html#import-maps,
/// https://w3c.github.io/webappsec-csp/#should-block-inline). The scan is deliberately narrower than
/// HTML: anything it cannot read one way is refused, so the map spactl hashes is the map a browser runs.
/// </summary>
internal static class ImportMap
{
    /// <summary>The one start tag a map may have: no other attribute, spelling, quoting or case.</summary>
    public const string StartTag = "<script type=\"importmap\">";

    private const string ScriptOpen = "<script";
    private const string ScriptClose = "</script";
    private const string CommentOpen = "<!--";

    /// <summary>
    /// The elements whose content the tokenizer reads as text, never as tags, with scripting on
    /// (https://html.spec.whatwg.org/multipage/parsing.html#parsing-html-fragments).
    /// </summary>
    private static readonly ImmutableArray<string> RawTextElements =
        ["title", "textarea", "style", "xmp", "iframe", "noembed", "noframes", "noscript", "plaintext"];

    /// <summary>The three hash algorithms CSP's hash-source grammar names, by their source prefix.</summary>
    private static readonly ImmutableArray<(string Prefix, Func<byte[], byte[]> Hash)> Algorithms =
    [
        ("'sha256-", SHA256.HashData),
        ("'sha384-", SHA384.HashData),
        ("'sha512-", SHA512.HashData),
    ];

    /// <summary>
    /// Scans a shell's <c>index.html</c>. <see cref="Scan.Refusal"/> is set when the document is
    /// refused; otherwise <see cref="Scan.Map"/> is the map's text with CR LF and lone CR made LF, or
    /// null for a document with no map, which passes.
    /// </summary>
    public static Scan Read(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        bool cspMeta = CarriesCspMeta(html);
        string? map = null;
        int at = 0;
        while (true)
        {
            int? next = NextScriptTag(html, at, out string? skippedRefusal);
            if (skippedRefusal is not null)
            {
                return Scan.Refused(skippedRefusal, cspMeta);
            }

            if (next is not int start)
            {
                break;
            }

            int close = TagEnd(html, start);
            if (close < 0)
            {
                return Scan.Refused("a <script tag is never closed with '>'.", cspMeta);
            }

            string tag = html[start..(close + 1)];
            int bodyEnd = ScriptEnd(html, close + 1);
            if (string.Equals(tag, StartTag, StringComparison.Ordinal))
            {
                if (map is not null)
                {
                    return Scan.Refused("it holds more than one import map; a document has one at most.", cspMeta);
                }

                if (bodyEnd < 0)
                {
                    return Scan.Refused("its import map has no </script> end tag.", cspMeta);
                }

                string body = html[(close + 1)..bodyEnd];
                if (BodyRefusal(body) is { } refusal)
                {
                    return Scan.Refused(refusal, cspMeta);
                }

                map = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            }
            else if (tag.Contains("importmap", StringComparison.OrdinalIgnoreCase))
            {
                return Scan.Refused(tag.Contains("src", StringComparison.OrdinalIgnoreCase)
                    ? "an import map names a src; a map is inline only, and its start tag is exactly " + StartTag + "."
                    : "a <script tag names importmap but is not exactly " + StartTag + ".", cspMeta);
            }
            else if (tag.Contains('&', StringComparison.Ordinal))
            {
                // A character reference can spell "importmap" in an attribute the scan would not see.
                return Scan.Refused("a <script tag contains '&', which could spell an import map the scan cannot read.", cspMeta);
            }

            // A script element's content is script data up to its end tag, never markup.
            at = bodyEnd < 0 ? close + 1 : bodyEnd + ScriptClose.Length;
        }

        return new Scan(map, null, cspMeta);
    }

    /// <summary>
    /// Whether a <c>script-src</c> list admits a map: one of its <c>'sha256-'</c>, <c>'sha384-'</c> or
    /// <c>'sha512-'</c> sources decodes to the map's digest under that algorithm. Matched on the decoded
    /// bytes, so padding and the base64url alphabet, both of which CSP's hash-source grammar allows,
    /// make no difference (https://w3c.github.io/webappsec-csp/#grammardef-hash-source).
    /// </summary>
    public static bool Admits(string map, IEnumerable<string> scriptSrc)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(scriptSrc);
        byte[] text = Encoding.ASCII.GetBytes(map);
        foreach (string source in scriptSrc)
        {
            foreach ((string prefix, Func<byte[], byte[]> hash) in Algorithms)
            {
                if (source.Length > prefix.Length + 1
                    && source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && source.EndsWith('\'')
                    && Decode(source[prefix.Length..^1]) is { } digest
                    && digest.AsSpan().SequenceEqual(hash(text)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The source a refusal tells the shell's owner to add: <c>'sha256-&lt;padded base64&gt;'</c>.</summary>
    public static string Sha256Source(string map) =>
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(map))) + "'";

    /// <summary>Whether a source is a hash source, which the write check compares between documents.</summary>
    public static bool IsHashSource(string source) =>
        Algorithms.Any(algorithm => source.StartsWith(algorithm.Prefix, StringComparison.OrdinalIgnoreCase));

    private static byte[]? Decode(string value)
    {
        string standard = value.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
        byte[] buffer = new byte[standard.Length];
        return Convert.TryFromBase64String(standard, buffer, out int written) ? buffer[..written] : null;
    }

    /// <summary>
    /// A body the scan will not hash: a character outside ASCII or a NUL, which a browser and a byte
    /// hash could read differently, or <c>&lt;!--</c> and <c>&lt;script</c>, which change how the
    /// HTML parser finds the end of script data
    /// (https://html.spec.whatwg.org/multipage/parsing.html#script-data-escaped-state).
    /// </summary>
    private static string? BodyRefusal(string body)
    {
        if (body.Contains('\0', StringComparison.Ordinal))
        {
            return "its import map contains a NUL character.";
        }

        if (body.Any(c => c > '\x7f'))
        {
            return "its import map contains a character outside ASCII.";
        }

        if (body.Contains("<!--", StringComparison.Ordinal))
        {
            return "its import map contains '<!--'.";
        }

        return body.Contains(ScriptOpen, StringComparison.OrdinalIgnoreCase)
            ? "its import map contains '<script'."
            : null;
    }

    /// <summary>
    /// The index of the <c>&lt;/script</c> end tag that ends script data at or after
    /// <paramref name="from"/>, or -1 when none does: the name must be followed by whitespace,
    /// <c>/</c> or <c>&gt;</c>, so <c>&lt;/scriptx</c> is data
    /// (https://html.spec.whatwg.org/multipage/parsing.html#script-data-end-tag-name-state).
    /// </summary>
    private static int ScriptEnd(string html, int from)
    {
        int index = from;
        while (html.IndexOf(ScriptClose, index, StringComparison.OrdinalIgnoreCase) is var end and >= 0)
        {
            int after = end + ScriptClose.Length;
            if (after < html.Length && IsTagNameEnd(html[after]))
            {
                return end;
            }

            index = after;
        }

        return -1;
    }

    /// <summary>
    /// The next <c>&lt;script</c> start tag at or after <paramref name="from"/>, matched as a tag name and
    /// outside a comment or a raw-text element: their text is never a tag, so a <c>&lt;script</c> in
    /// one must not make the scan skip a real map after it as script data
    /// (https://html.spec.whatwg.org/multipage/parsing.html#comment-start-state,
    /// https://html.spec.whatwg.org/multipage/parsing.html#rawtext-state).
    /// <paramref name="skippedRefusal"/> is set, and the search stops, at skipped text a browser could
    /// read as markup holding a map.
    /// </summary>
    private static int? NextScriptTag(string html, int from, out string? skippedRefusal)
    {
        skippedRefusal = null;
        int index = from;
        while (html.IndexOf(ScriptOpen, index, StringComparison.OrdinalIgnoreCase) is var script and >= 0)
        {
            int comment = html.IndexOf(CommentOpen, index, StringComparison.Ordinal);
            (int rawText, string? name) = NextRawText(html, index, script);
            if (comment >= 0 && comment < script && (name is null || comment < rawText))
            {
                index = CommentEnd(html, comment);
                if (html.AsSpan(comment, index - comment).Contains("importmap", StringComparison.OrdinalIgnoreCase))
                {
                    // Text the scan skips as a comment must not hold a map a browser could run.
                    skippedRefusal = "a comment names importmap, and the scan cannot tell whether a browser reads it as one.";
                    return null;
                }

                continue;
            }

            if (name is not null)
            {
                index = RawTextEnd(html, rawText, name);
                ReadOnlySpan<char> text = html.AsSpan(rawText, index - rawText);
                if (text.Contains(ScriptOpen, StringComparison.OrdinalIgnoreCase)
                    || text.Contains("importmap", StringComparison.OrdinalIgnoreCase))
                {
                    // Inside <svg> or <math> the same element's content is markup, and a tag such as <p>
                    // returns to HTML, so the text skipped as raw must hold no script and no map.
                    skippedRefusal = $"a <{name}> holds '<script' or names importmap, and the scan cannot tell whether a browser reads it as text.";
                    return null;
                }

                continue;
            }

            int after = script + ScriptOpen.Length;
            if (after >= html.Length || IsTagNameEnd(html[after]))
            {
                return script;
            }

            index = after;
        }

        return null;
    }

    /// <summary>The first raw-text start tag at or after <paramref name="from"/> and before <paramref name="before"/>, with its name.</summary>
    private static (int At, string? Name) NextRawText(string html, int from, int before)
    {
        (int at, string? found) = (before, null);
        foreach (string name in RawTextElements)
        {
            int index = from;
            while (html.IndexOf("<" + name, index, StringComparison.OrdinalIgnoreCase) is var open and >= 0 && open < at)
            {
                int after = open + 1 + name.Length;
                if (after < html.Length && IsTagNameEnd(html[after]))
                {
                    (at, found) = (open, name);
                    break;
                }

                index = after;
            }
        }

        return (at, found);
    }

    /// <summary>
    /// Where the raw-text element opened at <paramref name="open"/> ends: after its end tag, or at the end
    /// of the document when it has none, which is always so for <c>&lt;plaintext&gt;</c>.
    /// </summary>
    private static int RawTextEnd(string html, int open, string name)
    {
        string close = "</" + name;
        int index = open + 1 + name.Length;
        while (html.IndexOf(close, index, StringComparison.OrdinalIgnoreCase) is var end and >= 0)
        {
            index = end + close.Length;
            if (index < html.Length && IsTagNameEnd(html[index]))
            {
                return index;
            }
        }

        return html.Length;
    }

    /// <summary>Whether a character ends a tag name: whitespace, <c>/</c> or <c>&gt;</c>.</summary>
    private static bool IsTagNameEnd(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r' or '/' or '>';

    /// <summary>
    /// Where the comment opened at <paramref name="open"/> ends, as the tokenizer ends it: <c>&lt;!--&gt;</c>
    /// and <c>&lt;!---&gt;</c> close at once, otherwise the first <c>--&gt;</c> or <c>--!&gt;</c>, and a
    /// comment never closed runs to the end of the document
    /// (https://html.spec.whatwg.org/multipage/parsing.html#comment-start-state).
    /// </summary>
    private static int CommentEnd(string html, int open)
    {
        int text = open + CommentOpen.Length;
        if (html.AsSpan(text).StartsWith(">", StringComparison.Ordinal))
        {
            return text + 1;
        }

        if (html.AsSpan(text).StartsWith("->", StringComparison.Ordinal))
        {
            return text + 2;
        }

        int dashes = html.IndexOf("-->", text, StringComparison.Ordinal);
        int bang = html.IndexOf("--!>", text, StringComparison.Ordinal);
        return (dashes, bang) switch
        {
            ( < 0, < 0) => html.Length,
            ( >= 0, < 0) => dashes + 3,
            ( < 0, >= 0) => bang + 4,
            _ => dashes < bang ? dashes + 3 : bang + 4,
        };
    }

    /// <summary>
    /// The index of the <c>&gt;</c> that ends the start tag at <paramref name="start"/>, or -1 when none
    /// does: a <c>&gt;</c> inside a quoted attribute value does not end it, so
    /// <c>&lt;script data-x="&gt;" type="importmap"&gt;</c> is one tag that names importmap
    /// (https://html.spec.whatwg.org/multipage/parsing.html#before-attribute-value-state).
    /// </summary>
    private static int TagEnd(string html, int start)
    {
        bool afterEquals = false;
        for (int index = start + ScriptOpen.Length; index < html.Length; index++)
        {
            char c = html[index];
            if (c == '>')
            {
                return index;
            }

            if (afterEquals && c is '"' or '\'')
            {
                index = html.IndexOf(c, index + 1);
                if (index < 0)
                {
                    return -1;
                }

                afterEquals = false;
            }
            else if (c == '=')
            {
                afterEquals = true;
            }
            else if (c is not (' ' or '\t' or '\n' or '\f' or '\r'))
            {
                afterEquals = false;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the document carries a <c>&lt;meta http-equiv="Content-Security-Policy"&gt;</c>, which a
    /// browser enforces beside the header, so a map the header admits could still be blocked.
    /// </summary>
    private static bool CarriesCspMeta(string html)
    {
        int index = 0;
        while ((index = html.IndexOf("<meta", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int close = html.IndexOf('>', index);
            string tag = close < 0 ? html[index..] : html[index..close];
            if (tag.Contains("content-security-policy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            index += 5;
        }

        return false;
    }

    /// <summary>What <see cref="Read"/> found.</summary>
    internal sealed record Scan(string? Map, string? Refusal, bool CspMeta)
    {
        public static Scan Refused(string reason, bool cspMeta) => new(null, reason, cspMeta);
    }
}
