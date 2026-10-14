using System.Net;

namespace Gateway.TestHarness;

/// <summary>
/// The <c>Sec-Fetch-Site</c> computation of the Fetch Metadata specification
/// (https://w3c.github.io/webappsec-fetch-metadata/#sec-fetch-site-header), over origins rather than a
/// browser's request object.
///
/// The request gate judges <c>/api</c> and <c>/bff</c> by this header (contracts/request-gate.md), so
/// the test browser computes it the way a current browser does instead of letting each test pick a
/// value. A test that wants a value no browser would send overrides it explicitly.
/// </summary>
public static class FetchMetadata
{
    public const string SameOrigin = "same-origin";
    public const string SameSite = "same-site";
    public const string CrossSite = "cross-site";
    public const string None = "none";

    /// <summary>
    /// The header for a request made by <paramref name="initiator"/> whose URL list (the original URL and
    /// every redirect target) is <paramref name="urlList"/>. A null initiator is a navigation the user
    /// started from the address bar or a bookmark, which is <c>none</c> however it redirects.
    /// </summary>
    public static string Site(Uri? initiator, IEnumerable<Uri> urlList)
    {
        if (initiator is null)
        {
            return None;
        }

        string value = SameOrigin;
        foreach (Uri url in urlList)
        {
            if (IsSameOrigin(initiator, url))
            {
                continue;
            }

            if (!IsSameSite(initiator, url))
            {
                return CrossSite;
            }

            value = SameSite;
        }

        return value;
    }

    /// <summary>Scheme, host and port, compared as the URL standard does.</summary>
    public static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>
    /// Schemeful same-site: the same scheme and the same registrable domain, or the same host when the host
    /// is an IP address. Sibling SPAs (<c>app1.spa-poc.test</c> and <c>app2.spa-poc.test</c>) are same-site,
    /// which is exactly why the gate refuses <c>same-site</c> (ADR-0007, D7).
    /// </summary>
    public static bool IsSameSite(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RegistrableDomain(a.IdnHost), RegistrableDomain(b.IdnHost), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The registrable domain, with every public suffix taken to be one label. That holds for every name
    /// the hermetic tier uses: <c>.test</c>, <c>.invalid</c> and <c>.example</c> are reserved one-label
    /// suffixes (RFC 2606), and an IP address is its own site. It is not a public suffix list, and a test
    /// that needs one has outgrown this browser.
    /// </summary>
    public static string RegistrableDomain(string host)
    {
        string trimmed = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(trimmed, out _))
        {
            return trimmed;
        }

        string[] labels = trimmed.Split('.');
        return labels.Length <= 2 ? trimmed : string.Join('.', labels[^2..]);
    }

    /// <summary>The serialization of an origin, as the <c>Origin</c> header carries it.</summary>
    public static string Origin(Uri url) => url.GetLeftPart(UriPartial.Authority);
}
