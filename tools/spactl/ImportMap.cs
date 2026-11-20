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
        while (NextScriptTag(html, at) is int start)
        {
            int close = html.IndexOf('>', start);
            if (close < 0)
            {
                return Scan.Refused("a <script tag is never closed with '>'.", cspMeta);
            }

            string tag = html[start..(close + 1)];
            int bodyEnd = html.IndexOf(ScriptClose, close + 1, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>The next <c>&lt;script</c> start tag at or after <paramref name="from"/>, matched as a tag name.</summary>
    private static int? NextScriptTag(string html, int from)
    {
        int index = from;
        while ((index = html.IndexOf(ScriptOpen, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = index + ScriptOpen.Length;
            if (after >= html.Length || html[after] is ' ' or '\t' or '\n' or '\f' or '\r' or '/' or '>')
            {
                return index;
            }

            index = after;
        }

        return null;
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
