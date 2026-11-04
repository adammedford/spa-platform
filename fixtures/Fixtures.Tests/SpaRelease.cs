using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// One fixture SPA's directory, read as the release <c>spactl release publish</c> uploads it: every
/// file under it lands at <c>apps/{appId}/releases/{release}/</c> with the same relative path
/// (contracts/s3-layout.md).
/// </summary>
internal sealed partial record SpaRelease(string AppId, string Root)
{
    internal static SpaRelease Of(string appId) => new(appId, FixtureFiles.Path("fixtures", "spa", appId));

    internal string IndexHtml => File.ReadAllText(Path.Combine(Root, "index.html"));

    /// <summary>Every file in the release, as a forward-slash path relative to its root.</summary>
    internal IEnumerable<string> Files =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                 .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/'))
                 .Order(StringComparer.Ordinal);

    /// <summary>The files under <c>assets/</c>, which the gateway serves as immutable for a year.</summary>
    internal IEnumerable<string> Assets => Files.Where(file => file.StartsWith("assets/", StringComparison.Ordinal));

    /// <summary>The scripts index.html loads, as the paths it names.</summary>
    internal IEnumerable<string> ScriptSources =>
        ScriptSrc().Matches(IndexHtml).Select(match => match.Groups[1].Value);

    /// <summary>The text of every script the shell loads, so a policy check sees what will run.</summary>
    internal string Scripts => string.Join(
        '\n', ScriptSources.Select(source => File.ReadAllText(Path.Combine(Root, source.TrimStart('/')))));

    /// <summary>An attribute of the <c>&lt;main id="app"&gt;</c> element, which carries the app's settings to its script.</summary>
    internal string? MainAttribute(string name)
    {
        Match main = MainElement().Match(IndexHtml);
        if (!main.Success)
        {
            return null;
        }

        Match attribute = Regex.Match(main.Value, $"\\b{Regex.Escape(name)}=\"([^\"]*)\"", RegexOptions.None, RegexTimeout.Value);
        return attribute.Success ? attribute.Groups[1].Value : null;
    }

    /// <summary>The first eight hex digits of the SHA-256 of a release file's bytes.</summary>
    internal string ContentHash(string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, relativePath))))[..8];

    [GeneratedRegex("<script\\b[^>]*\\bsrc=\"([^\"]+)\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex("<main\\b[^>]*\\bid=\"app\"[^>]*>", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex MainElement();
}
