namespace Platform.Hosting;

/// <summary>
/// The request path checked before any of it becomes part of an S3 key (N-13; contracts/s3-layout.md:
/// "never from an unchecked path segment"). Two checks, both pure functions: one of the request target
/// as the client sent it, one of the decoded path the key is built from.
/// </summary>
internal static class AssetPath
{
    /// <summary>
    /// The target as sent, before Kestrel decodes it and removes dot segments
    /// (https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.features.ihttprequestfeature.rawtarget).
    /// Kestrel turns <c>/./index.html</c> into <c>/index.html</c>, which stays inside the prefix but
    /// is still a dot segment N-13 says is refused, so the shape is judged here, where it is visible.
    ///
    /// Refused: anything but the origin form; a backslash; an escaped <c>.</c>, <c>/</c>, <c>\</c> or
    /// NUL; an empty segment but the last; a <c>.</c> or <c>..</c> segment, with or without a
    /// <c>;</c> parameter after it, which some servers strip before resolving. Ordinary escapes such
    /// as <c>%20</c> pass, and the decoded path is judged again by <see cref="Validate"/>.
    /// </summary>
    public static bool RawIsNormal(string raw)
    {
        if (raw.Length == 0 || raw[0] != '/')
        {
            return false;
        }

        int query = raw.IndexOf('?', StringComparison.Ordinal);
        string path = query < 0 ? raw : raw[..query];

        if (path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%00", StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = path[1..].Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            int parameter = segment.IndexOf(';', StringComparison.Ordinal);
            string name = parameter < 0 ? segment : segment[..parameter];

            if ((segment.Length == 0 && i < segments.Length - 1) || name is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The path relative to the release prefix, without its leading slash, or null when it fails.
    /// The empty string is the root, <c>/</c>.
    ///
    /// Refused: no leading slash; an empty segment anywhere but the end (a doubled slash); a
    /// <c>.</c> or <c>..</c> segment; a backslash, which some servers and S3 tooling read as a
    /// separator; a <c>%</c>, because the server decodes every escape but <c>%2F</c> before the path
    /// reaches here ("The path is fully decoded by the server except for '%2F'",
    /// https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.features.ihttprequestfeature.path),
    /// so a <c>%</c> that remains is an encoded slash, a second round of encoding such as
    /// <c>%252e</c>, or a literal <c>%</c> sent as <c>%25</c>. The first two are N-13's shapes; the
    /// third costs a release the use of <c>%</c> in a file name, which is refused rather than told
    /// apart from them; and any control character, NUL included.
    /// </summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        foreach (char c in path)
        {
            if (c is '\\' or '%' || char.IsControl(c))
            {
                return null;
            }
        }

        string relative = path[1..];
        string[] segments = relative.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool last = i == segments.Length - 1;

            if ((segment.Length == 0 && !last) || segment is "." or "..")
            {
                return null;
            }
        }

        return relative;
    }

    /// <summary>
    /// True when the last segment has a dot after its first character and before its last:
    /// <c>app.js</c> has one; <c>orders</c>, the dot file <c>.well-known</c> and <c>v1.</c> have none.
    /// A trailing slash leaves an empty last segment, which has none.
    /// </summary>
    public static bool HasExtension(string relative)
    {
        string last = relative[(relative.LastIndexOf('/') + 1)..];
        int dot = last.LastIndexOf('.');
        return dot > 0 && dot < last.Length - 1;
    }
}
