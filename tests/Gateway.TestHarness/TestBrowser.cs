using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Gateway.TestHarness;

/// <summary>
/// A scripted browser for the hermetic tier. It does the two things the gateway's rules depend on and an
/// <see cref="HttpClient"/> does not: it keeps cookies per host, and it sends the Fetch Metadata headers
/// a current browser sends, computed from where the request came from (<see cref="FetchMetadata"/>).
///
/// <para>
/// Three ways to make a request, because the gate treats them differently:
/// <list type="bullet">
/// <item><see cref="NavigateAsync"/>: the user typed the URL. <c>Sec-Fetch-Site: none</c>, mode
/// <c>navigate</c>, destination <c>document</c>.</item>
/// <item><see cref="ClickAsync"/> and <see cref="SubmitFormAsync"/>: a navigation the current page
/// started, so the site is computed against the page's origin. A POST form sends <c>Origin</c>.</item>
/// <item><see cref="FetchAsync"/>: <c>fetch()</c> from the current page. Mode <c>cors</c>, destination
/// <c>empty</c>, and <c>Origin</c> on anything but a same-origin <c>GET</c> or <c>HEAD</c>.</item>
/// </list>
/// Navigations follow redirects, recomputing the site over the whole redirect chain as the specification
/// says, so a login that went through the provider arrives at the callback <c>cross-site</c>. A fetch
/// follows same-origin redirects only; where a browser would fail a cross-origin redirect in
/// <c>cors</c> mode, this returns the 3xx so the test can say what it expected instead.
/// </para>
/// <para>
/// What it does not do, on purpose, because the deployed tier proves it in a real browser
/// (docs/plan/04-tests.md): it enforces no cookie prefix and no <c>SameSite</c>, runs no script, and
/// applies no content security policy. A planted cookie with an exact <c>__Host-</c> name is accepted
/// here, as finding R-D3 records.
/// </para>
/// <para>
/// Every exchange is kept in <see cref="Exchanges"/>, bodies included, so F-05 and N-23 can search every
/// response the browser saw. Nothing here writes a log line: cookie values and bodies stay in memory.
/// </para>
/// </summary>
public sealed class TestBrowser : IDisposable
{
    /// <summary>A browser follows at most twenty redirects (https://fetch.spec.whatwg.org/#http-redirect-fetch).</summary>
    public const int MaxRedirects = 20;

    private const string NavigationAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string UserAgent = "SpaPlatformTestBrowser/1.0";

    private readonly HttpMessageInvoker transport;
    private readonly List<BrowserResponse> exchanges = [];

    /// <param name="transport">
    /// Where requests go. The harness passes one that sends a gateway host to the gateway through a
    /// stand-in for the load balancer and a loopback address straight to its listener.
    /// </param>
    public TestBrowser(HttpMessageHandler transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.transport = new HttpMessageInvoker(transport, disposeHandler: true);
    }

    /// <summary>The cookie jar. Host-only cookies stay with their host; a <c>Domain</c> cookie reaches its subdomains.</summary>
    public CookieContainer Cookies { get; } = new();

    /// <summary>The URL of the current document: where the last navigation ended. Null before the first.</summary>
    public Uri? Page { get; private set; }

    /// <summary>Every response the browser received, redirects included, oldest first.</summary>
    public IReadOnlyList<BrowserResponse> Exchanges
    {
        get
        {
            lock (exchanges)
            {
                return [.. exchanges];
            }
        }
    }

    /// <summary>Every cookie in the jar, for a test that dumps them (F-05).</summary>
    public IReadOnlyList<Cookie> AllCookies => [.. Cookies.GetAllCookies().Cast<Cookie>()];

    /// <summary>The user types <paramref name="url"/> into the address bar.</summary>
    public Task<BrowserResponse> NavigateAsync(Uri url, RequestOptions? options = null, CancellationToken ct = default) =>
        SendNavigationAsync(HttpMethod.Get, url, initiator: null, body: null, options, ct);

    /// <summary>The user types <paramref name="url"/> into the address bar.</summary>
    public Task<BrowserResponse> NavigateAsync(string url, RequestOptions? options = null, CancellationToken ct = default) =>
        NavigateAsync(new Uri(url, UriKind.Absolute), options, ct);

    /// <summary>The user follows a link on the current page. A relative URL resolves against the page.</summary>
    public Task<BrowserResponse> ClickAsync(string url, RequestOptions? options = null, CancellationToken ct = default)
    {
        Uri page = RequirePage();
        return SendNavigationAsync(HttpMethod.Get, new Uri(page, url), page, body: null, options, ct);
    }

    /// <summary>The current page submits a form, URL-encoded, by POST unless another method is given.</summary>
    public Task<BrowserResponse> SubmitFormAsync(
        string action,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        HttpMethod? method = null,
        RequestOptions? options = null,
        CancellationToken ct = default)
    {
        Uri page = RequirePage();
        method ??= HttpMethod.Post;
        Uri target = new(page, action);
        string encoded = string.Join('&', (fields ?? []).Select(field =>
            Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));

        if (method == HttpMethod.Get)
        {
            UriBuilder builder = new(target) { Query = encoded };
            return SendNavigationAsync(method, builder.Uri, page, body: null, options, ct);
        }

        return SendNavigationAsync(method, target, page,
            new Body(Encoding.UTF8.GetBytes(encoded), "application/x-www-form-urlencoded"), options, ct);
    }

    /// <summary><c>fetch()</c> from the current page. A relative URL resolves against the page.</summary>
    public async Task<BrowserResponse> FetchAsync(
        HttpMethod method,
        string url,
        HttpContent? content = null,
        RequestOptions? options = null,
        CancellationToken ct = default)
    {
        Uri page = RequirePage();
        Uri target = new(page, url);
        Body? body = content is null
            ? null
            : new Body(await content.ReadAsByteArrayAsync(ct), content.Headers.ContentType?.ToString());

        return await SendAsync(method, target, page, body, options, navigation: false, ct);
    }

    /// <summary>A fetch with no body, for the common case.</summary>
    public Task<BrowserResponse> GetAsync(string url, RequestOptions? options = null, CancellationToken ct = default) =>
        FetchAsync(HttpMethod.Get, url, content: null, options, ct);

    public void Dispose() => transport.Dispose();

    private Task<BrowserResponse> SendNavigationAsync(
        HttpMethod method, Uri url, Uri? initiator, Body? body, RequestOptions? options, CancellationToken ct) =>
        SendAsync(method, url, initiator, body, options, navigation: true, ct);

    private async Task<BrowserResponse> SendAsync(
        HttpMethod method, Uri url, Uri? initiator, Body? body, RequestOptions? options, bool navigation, CancellationToken ct)
    {
        List<Uri> chain = [];
        Uri current = url;
        bool taintedOrigin = false;
        for (int hop = 0; ; hop++)
        {
            // Fetch redirect-taint compares consecutive URLs, and only taints when the previous
            // URL also differs from the initiator. A first cross-origin form POST retains its origin.
            // https://fetch.spec.whatwg.org/#concept-request-redirect-taint
            if (initiator is not null && chain.Count > 0
                && !FetchMetadata.IsSameOrigin(chain[^1], current)
                && !FetchMetadata.IsSameOrigin(initiator, chain[^1]))
            {
                taintedOrigin = true;
            }

            chain.Add(current);
            Dictionary<string, string?> headers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = navigation ? NavigationAccept : "*/*",
                ["Sec-Fetch-Site"] = FetchMetadata.Site(initiator, chain),
                ["Sec-Fetch-Mode"] = navigation ? "navigate" : "cors",
                ["Sec-Fetch-Dest"] = navigation ? "document" : "empty",
            };
            if (navigation)
            {
                headers["Sec-Fetch-User"] = "?1";
                headers["Upgrade-Insecure-Requests"] = "1";
            }

            // https://fetch.spec.whatwg.org/#origin-header
            if (initiator is not null && ((method != HttpMethod.Get && method != HttpMethod.Head)
                || (!navigation && chain.Any(hopUrl => !FetchMetadata.IsSameOrigin(initiator, hopUrl)))))
            {
                headers["Origin"] = taintedOrigin ? "null" : FetchMetadata.Origin(initiator);
            }

            BrowserResponse response = await SendOnceAsync(method, current, headers, body, options, chain, ct);
            if (!IsRedirect(response, out Uri? next) || options?.Redirect == RedirectMode.Manual
                || hop + 1 >= MaxRedirects || (!navigation && !FetchMetadata.IsSameOrigin(initiator!, next)))
            {
                if (navigation)
                {
                    Page = current;
                }

                return response;
            }

            (method, body) = AfterRedirect(response.StatusCode, method, body);
            current = next;
        }
    }

    private async Task<BrowserResponse> SendOnceAsync(
        HttpMethod method,
        Uri url,
        Dictionary<string, string?> headers,
        Body? body,
        RequestOptions? options,
        List<Uri> chain,
        CancellationToken ct)
    {
        foreach (KeyValuePair<string, string?> header in options?.Headers ?? ImmutableDictionary<string, string?>.Empty)
        {
            headers[header.Key] = header.Value;
        }

        using HttpRequestMessage request = new(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        string cookie = Cookies.GetCookieHeader(url);
        if (cookie.Length > 0 && !headers.ContainsKey("Cookie"))
        {
            headers["Cookie"] = cookie;
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body.Bytes);
            if (body.ContentType is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
            }
        }

        foreach ((string name, string? value) in headers)
        {
            if (value is null)
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // Record accepted request headers before the ALB transport adds its forwarding headers.
        ImmutableDictionary<string, string> sent = request.Headers
            .Concat(request.Content?.Headers.AsEnumerable() ?? [])
            .ToImmutableDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

        using HttpResponseMessage message = await transport.SendAsync(request, ct);
        byte[] bytes = await message.Content.ReadAsByteArrayAsync(ct);

        ImmutableDictionary<string, ImmutableArray<string>>.Builder received =
            ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in message.Headers.Concat(message.Content.Headers))
        {
            received[header.Key] = received.TryGetValue(header.Key, out ImmutableArray<string> existing)
                ? existing.AddRange(header.Value)
                : [.. header.Value];
        }

        if (message.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            foreach (string setCookie in setCookies)
            {
                Store(url, setCookie);
            }
        }

        BrowserResponse response = new(
            method.Method, url, sent, message.StatusCode, received.ToImmutable(), bytes, [.. chain.Take(chain.Count - 1)]);

        lock (exchanges)
        {
            exchanges.Add(response);
        }

        return response;
    }

    /// <summary>A cookie the jar refuses (a <c>Domain</c> that does not match, say) is dropped, as a browser drops it.</summary>
    private void Store(Uri url, string setCookie)
    {
        try
        {
            Cookies.SetCookies(url, setCookie);
        }
        catch (CookieException)
        {
        }
    }

    private static bool IsRedirect(BrowserResponse response, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? next)
    {
        next = null;
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            return false;
        }

        string? location = response.Header("Location");
        if (location is null || !Uri.TryCreate(response.Url, location, out next))
        {
            return false;
        }

        return next.Scheme is "http" or "https";
    }

    /// <summary>303, and 301 or 302 after a POST, turn into a GET with no body (https://fetch.spec.whatwg.org/#http-redirect-fetch).</summary>
    private static (HttpMethod Method, Body? Body) AfterRedirect(HttpStatusCode status, HttpMethod method, Body? body) =>
        ((int)status == 303 && method != HttpMethod.Get && method != HttpMethod.Head)
        || ((int)status is 301 or 302 && method == HttpMethod.Post)
            ? (HttpMethod.Get, null)
            : (method, body);

    private Uri RequirePage() =>
        Page ?? throw new InvalidOperationException("the browser has no page yet. Navigate somewhere first.");

    private sealed record Body(byte[] Bytes, string? ContentType);
}

/// <summary>Whether a request follows redirects, as <c>fetch()</c>'s <c>redirect</c> option says.</summary>
public enum RedirectMode
{
    Follow,
    Manual,
}

/// <summary>
/// What a test changes about one request. A header in <see cref="Headers"/> replaces the one the browser
/// would send, and a null value removes it: <c>["Sec-Fetch-Site"] = null</c> is a request with no
/// Fetch Metadata, which is what N-07 sends.
/// </summary>
public sealed record RequestOptions
{
    public IReadOnlyDictionary<string, string?> Headers { get; init; } = ImmutableDictionary<string, string?>.Empty;

    public RedirectMode Redirect { get; init; } = RedirectMode.Follow;

    /// <summary>Options that only change headers.</summary>
    public static RequestOptions With(params (string Name, string? Value)[] headers) => new()
    {
        Headers = headers.ToImmutableDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// One response the browser received, with the request that produced it. Headers are kept whole; the
/// body is kept as bytes.
/// </summary>
/// <param name="RedirectedFrom">The URLs requested before this one in the same navigation or fetch.</param>
public sealed record BrowserResponse(
    string Method,
    Uri Url,
    ImmutableDictionary<string, string> RequestHeaders,
    HttpStatusCode StatusCode,
    ImmutableDictionary<string, ImmutableArray<string>> Headers,
    byte[] Bytes,
    ImmutableArray<Uri> RedirectedFrom)
{
    /// <summary>The body as UTF-8 text.</summary>
    public string Body => Encoding.UTF8.GetString(Bytes);

    /// <summary>A response header, its values joined with ", ", or null when absent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out ImmutableArray<string> values) ? string.Join(", ", values) : null;

    /// <summary>A header this request was sent with, or null.</summary>
    public string? Sent(string name) => RequestHeaders.TryGetValue(name, out string? value) ? value : null;

    /// <summary>The <c>Set-Cookie</c> header lines of this response.</summary>
    public IReadOnlyList<string> SetCookies =>
        Headers.TryGetValue("Set-Cookie", out ImmutableArray<string> values) ? values : [];

    /// <summary>Names the exchange without its body or cookie values, so it is safe in assertion output.</summary>
    public override string ToString() => $"{Method} {Url} -> {(int)StatusCode}";
}
