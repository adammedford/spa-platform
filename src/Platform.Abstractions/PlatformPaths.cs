using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// The path rules the gateway and <c>spactl</c> both apply, so that the two cannot read a path two
/// ways: which paths the platform reserves, where a fragment's namespace, callback and entry module
/// are, and what an entry file name and a fragment release may contain. contracts/http-contract.md ->
/// Endpoints and contracts/s3-layout.md are normative; the fragments spec (#961) is the source.
///
/// "Reserved" has two meanings, kept apart here as in the contract. A path reserved
/// <i>outright</i> is never served from any release: <c>/bff/**</c>, <c>/api/**</c>,
/// <c>/signin-oidc/**</c>, the two sign-out callbacks, the health paths, and
/// <c>/fragments/{fragmentId}/api/**</c>. A path reserved <i>from the shell's release</i> is served
/// by the platform and never from the shell's prefix: <c>/fragments/**</c>, on every host, which a
/// host with no fragments answers 404 <c>path.reserved</c>. Segments match without regard to case, as
/// endpoint routing and the request gate match them.
/// </summary>
public static class PlatformPaths
{
    /// <summary>The prefixes reserved outright on every host, the fragment API namespace aside.</summary>
    public static readonly ImmutableArray<string> Reserved =
    [
        "/bff", "/api", "/signin-oidc", "/signout-callback-oidc", "/signout-oidc", "/healthz", "/readyz",
    ];

    /// <summary>The prefix reserved from the shell's release on every host.</summary>
    public const string Fragments = "/fragments";

    /// <summary>The entry module's file name when a fragment's entry names none.</summary>
    public const string DefaultEntry = "entry.js";

    /// <summary>The longest entry file name, in characters.</summary>
    public const int EntryMaxLength = 64;

    /// <summary>A fragment's callback path: <c>/signin-oidc/{fragmentId}</c>, GET only.</summary>
    public static PathString FragmentCallback(string fragmentId) => new("/signin-oidc/" + fragmentId);

    /// <summary>A fragment's API prefix: <c>/fragments/{fragmentId}/api</c>, stripped as <c>/api</c> is.</summary>
    public static PathString FragmentApi(string fragmentId) => new(Fragments + "/" + fragmentId + Api);

    /// <summary>
    /// The URL <c>GET /bff/fragments</c> lists for a fragment: <c>/fragments/{fragmentId}/{entry}</c>.
    /// It names no release; the path resolves to the live one.
    /// </summary>
    [SuppressMessage("Design", "CA1055:URI return values should not be strings",
        Justification = "#988 part 8 and #961's GET /bff/fragments: a host-relative path the body lists as a "
            + "JSON string; a relative Uri would round-trip it through parsing for nothing.")]
    public static string EntryUrl(string fragmentId, string entry) => Fragments + "/" + fragmentId + "/" + entry;

    /// <summary>
    /// Whether a path is reserved outright: under one of <see cref="Reserved"/>, or under
    /// <c>/fragments/{seg}/api</c> for any segment.
    /// </summary>
    public static bool IsReservedOutright(PathString path)
    {
        foreach (string reserved in Reserved)
        {
            if (path.StartsWithSegments(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return FragmentOf(path) is { } fragment
            && fragment.Remainder.StartsWithSegments(Api, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a path is under <see cref="Fragments"/>, which no shell release serves.</summary>
    public static bool IsReservedFromShell(PathString path) =>
        path.StartsWithSegments(Fragments, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first segment under <c>/fragments/</c> and the path after it, or null for a path not under
    /// <c>/fragments/{seg}</c>. <c>/fragments/orders-ui/assets/a.js</c> is
    /// <c>("orders-ui", "/assets/a.js")</c>. It checks the shape only: whether the segment is a
    /// fragment of the resolved entry is host resolution's to decide.
    /// </summary>
    public static (string FragmentId, PathString Remainder)? FragmentOf(PathString path)
    {
        if (!path.StartsWithSegments(Fragments, StringComparison.OrdinalIgnoreCase, out PathString remaining)
            || !remaining.HasValue)
        {
            return null;
        }

        // remaining starts with "/": "/{seg}" or "/{seg}/rest".
        string value = remaining.Value!;
        int end = value.IndexOf('/', 1);
        string segment = end < 0 ? value[1..] : value[1..end];
        if (segment.Length == 0)
        {
            return null;
        }

        return (segment, end < 0 ? PathString.Empty : new PathString(value[end..]));
    }

    /// <summary>
    /// Whether a name may be an entry file: at most <see cref="EntryMaxLength"/> characters, matching
    /// <c>^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)*\.js$</c>, so a release-root file never under
    /// <c>assets/</c> or <c>api/</c>.
    /// </summary>
    public static bool IsEntryName(string name)
    {
        // The pattern, without a regular expression: a non-empty stem before ".js", split on '.', each
        // part non-empty and of [A-Za-z0-9_-], the first starting with a letter or digit.
        const string Suffix = ".js";
        if (name is null || name.Length > EntryMaxLength || !name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = name[..^Suffix.Length].Split('.');
        return char.IsAsciiLetterOrDigit(parts[0].FirstOrDefault())
            && Array.TrueForAll(parts, part => part.Length > 0 && part.All(IsEntryChar));
    }

    private static bool IsEntryChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    /// <summary>
    /// Whether a key relative to a fragment release's root may be published in it: false for any key
    /// whose first segment is <c>api</c> in any letter case, which the fragment's API namespace
    /// reserves outright, matched as <see cref="IsReservedOutright"/> matches it.
    /// </summary>
    public static bool IsPublishableInFragment(string relativeKey)
    {
        ArgumentNullException.ThrowIfNull(relativeKey);
        return !new PathString("/" + relativeKey).StartsWithSegments(Api, StringComparison.OrdinalIgnoreCase);
    }

    private const string Api = "/api";
}
