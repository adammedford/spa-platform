namespace Gateway.E2ETests.Stack;

/// <summary>
/// A URL as a failure message may print it: scheme, authority and path, never the query or the
/// fragment. On the cell a login's URLs carry the provider's <c>state</c> and the callback's
/// authorization <c>code</c>, and a sitting posts its output on an issue; AGENTS.md has no code or
/// state value printed anywhere.
/// </summary>
internal static class Redacted
{
    public static string Url(Uri url) => url.GetLeftPart(UriPartial.Path);

    /// <summary>A page's URL as Playwright reports it; one that does not parse is not printed at all.</summary>
    public static string Url(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? Url(parsed) : "(a URL that does not parse)";

    /// <summary>The URLs in order, each as <see cref="Url(Uri)"/> prints it, joined with " -> ".</summary>
    public static string Urls(IEnumerable<Uri> urls) => string.Join(" -> ", urls.Select(Url));
}
