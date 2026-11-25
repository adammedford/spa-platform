using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>A login followed one redirect at a time, so a test can act between two hops.</summary>
internal static class Steps
{
    public static readonly RequestOptions Manual = new() { Redirect = RedirectMode.Manual };

    /// <summary>Where <paramref name="response"/> redirects, resolved against its URL, or null.</summary>
    public static Uri? Next(BrowserResponse response) =>
        response.Header("Location") is { } location ? new Uri(response.Url, location) : null;

    /// <summary>One navigation to <paramref name="url"/>, not followed.</summary>
    public static Task<BrowserResponse> ToAsync(TestBrowser browser, Uri url, CancellationToken ct) =>
        browser.NavigateAsync(url, Manual, ct);

    /// <summary>
    /// Follows <paramref name="from"/>'s redirects one at a time until the next one satisfies
    /// <paramref name="stop"/>, and returns the response that redirects there.
    /// </summary>
    public static async Task<BrowserResponse> UntilAsync(TestBrowser browser, BrowserResponse from, Func<Uri, bool> stop, CancellationToken ct)
    {
        BrowserResponse at = from;
        for (int hop = 0; hop < TestBrowser.MaxRedirects; hop++)
        {
            Uri next = Next(at) ?? throw new InvalidOperationException($"the journey ended at {at} before the hop it was looking for.");
            if (stop(next))
            {
                return at;
            }

            at = await ToAsync(browser, next, ct);
        }

        throw new InvalidOperationException("too many redirects.");
    }

    /// <summary>Whether <paramref name="url"/> is the provider's authorization request for <paramref name="clientId"/>.</summary>
    public static bool IsAuthorizeFor(Uri url, string clientId) =>
        url.AbsolutePath == "/authorize" && url.Query.Contains("client_id=" + Uri.EscapeDataString(clientId) + "&", StringComparison.Ordinal);

    /// <summary>The <c>client_id</c> an authorization request names.</summary>
    public static string? ClientOf(Uri authorize) =>
        authorize.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == "client_id")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    /// <summary>A manual login on the shell, stopped where its first <c>/bff/chain</c> step would be sent: that step's URL.</summary>
    public static async Task<Uri> FirstChainStepAsync(TestBrowser browser, HarnessApp shell, string returnUrl, CancellationToken ct)
    {
        BrowserResponse start = await ToAsync(browser, shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)), ct);
        BrowserResponse before = await UntilAsync(browser, start, url => url.AbsolutePath == "/bff/chain", ct);
        return Next(before)!;
    }
}
