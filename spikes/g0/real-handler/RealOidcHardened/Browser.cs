using System.Net;

namespace Spike;

/// <summary>
/// A browser as far as cookies and redirects go. Unlike the spike's client this jar honours the cookie Path
/// attribute, so a cookie scoped to /signin-oidc is only sent there. Requests reach the gateway on loopback
/// with the SPA's Host header and X-Forwarded-Proto: https, as behind a TLS-terminating load balancer.
/// </summary>
public sealed class Browser(string gatewayBaseUrl, string idpBaseUrl)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Uri _gateway = new(gatewayBaseUrl);
    private readonly Uri _idp = new(idpBaseUrl);

    public sealed record Cookie(string Name, string Value, string Path, string Raw);

    public readonly Dictionary<string, Dictionary<string, Cookie>> Jar = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Host, string SetCookie)> SetCookies = [];

    /// <summary>The user the test "types in" at the identity provider's login page.</summary>
    public string LoginAs = "alice";

    private Dictionary<string, Cookie> JarFor(string host)
    {
        if (!Jar.TryGetValue(host, out var c)) Jar[host] = c = new Dictionary<string, Cookie>(StringComparer.Ordinal);
        return c;
    }

    private static bool PathMatches(string cookiePath, string requestPath)
        => cookiePath == requestPath
           || (requestPath.StartsWith(cookiePath, StringComparison.Ordinal) && (cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/'));

    /// <summary>Cookies the browser would attach to a request for <paramref name="path"/> on <paramref name="host"/>.</summary>
    public Dictionary<string, string> CookiesFor(string host, string path = "/")
        => JarFor(host).Values.Where(c => PathMatches(c.Path, path)).ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal);

    public IEnumerable<Cookie> AllCookies(string host) => JarFor(host).Values;

    /// <summary>Put a cookie in the jar without the server's help: page script, or a sibling host's Domain= cookie.</summary>
    public void Plant(string host, string name, string value, string path = "/") => JarFor(host)[name] = new Cookie(name, value, path, "(planted)");

    public Task<HttpResponseMessage> Get(string url, IDictionary<string, string>? cookieOverride = null, IDictionary<string, string>? headers = null)
        => Send(HttpMethod.Get, url, cookieOverride, null, headers);

    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, IDictionary<string, string>? cookieOverride = null, HttpContent? content = null, IDictionary<string, string>? headers = null)
    {
        var u = new Uri(url);
        HttpRequestMessage req;
        if (u.Authority == _idp.Authority)
        {
            req = new HttpRequestMessage(method, u);
        }
        else
        {
            req = new HttpRequestMessage(method, new Uri(_gateway, u.PathAndQuery));
            req.Headers.Host = u.Authority;
            req.Headers.Add("X-Forwarded-Proto", u.Scheme);
            var cookies = cookieOverride ?? CookiesFor(u.Authority, u.AbsolutePath);
            if (cookies.Count > 0) req.Headers.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        if (headers is not null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        req.Content = content;

        var resp = await Http.SendAsync(req);
        if (u.Authority != _idp.Authority && resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                lock (SetCookies) SetCookies.Add((u.Authority, sc));
                if (cookieOverride is not null) continue;               // replayed requests do not touch the jar
                var parts = sc.Split(';').Select(p => p.Trim()).ToArray();
                var eq = parts[0].IndexOf('=');
                string name = parts[0][..eq], value = parts[0][(eq + 1)..];
                var path = parts.Skip(1).FirstOrDefault(p => p.StartsWith("path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "/";
                var deleted = value.Length == 0 || sc.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (deleted) JarFor(u.Authority).Remove(name); else JarFor(u.Authority)[name] = new Cookie(name, value, path, sc);
            }
        }
        return resp;
    }

    private string AtIdp(string url)
        => new Uri(url).Authority == _idp.Authority && url.Contains("/authorize?") && LoginAs != "alice" ? url + "&login_hint=" + Uri.EscapeDataString(LoginAs) : url;

    /// <summary>Follow redirects like a top-level navigation. Returns the final response and every URL visited.</summary>
    public async Task<(HttpResponseMessage Final, List<string> Hops)> Navigate(string url, int maxHops = 12, Func<string, bool>? stopBefore = null)
    {
        var hops = new List<string>();
        var current = url;
        HttpResponseMessage? last = null;
        for (var i = 0; i < maxHops; i++)
        {
            current = AtIdp(current);
            if (stopBefore is not null && last is not null && stopBefore(current)) { hops.Add(current); return (last, hops); }
            hops.Add(current);
            var resp = last = await Get(current);
            if (resp.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect))
                return (resp, hops);
            var location = resp.Headers.Location!;
            current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
        }
        throw new InvalidOperationException("redirect loop: " + string.Join(" -> ", hops));
    }
}
