using System.Net;

namespace Spike;

/// <summary>
/// A browser, as far as cookies and redirects go. URLs are the ones a user would see (https://app1.spa.test/...);
/// requests are delivered to the gateway on loopback with that Host header and X-Forwarded-Proto: https, which is
/// what the gateway sees behind a TLS-terminating load balancer. Cookies are kept per host, like host-only cookies.
/// </summary>
public sealed class Browser(string gatewayBaseUrl, string idpBaseUrl)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Uri _gateway = new(gatewayBaseUrl);
    private readonly Uri _idp = new(idpBaseUrl);

    public readonly Dictionary<string, Dictionary<string, string>> Jar = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Host, string SetCookie)> SetCookies = [];

    public Dictionary<string, string> CookiesFor(string host)
    {
        if (!Jar.TryGetValue(host, out var c)) Jar[host] = c = new Dictionary<string, string>(StringComparer.Ordinal);
        return c;
    }

    public Task<HttpResponseMessage> Get(string url, IDictionary<string, string>? cookieOverride = null)
        => Send(HttpMethod.Get, url, cookieOverride);

    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, IDictionary<string, string>? cookieOverride = null)
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
            var cookies = cookieOverride ?? CookiesFor(u.Authority);
            if (cookies.Count > 0) req.Headers.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        var resp = await Http.SendAsync(req);
        if (u.Authority != _idp.Authority && resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                lock (SetCookies) SetCookies.Add((u.Authority, sc));
                if (cookieOverride is not null) continue;               // replayed requests do not touch the jar
                var first = sc.Split(';', 2)[0];
                var eq = first.IndexOf('=');
                string name = first[..eq], value = first[(eq + 1)..];
                var deleted = value.Length == 0 || sc.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (deleted) CookiesFor(u.Authority).Remove(name); else CookiesFor(u.Authority)[name] = value;
            }
        }
        return resp;
    }

    /// <summary>Follow redirects the way a top-level navigation would. Returns the final response and every URL visited.</summary>
    public async Task<(HttpResponseMessage Final, List<string> Hops)> Navigate(string url, int maxHops = 12)
    {
        var hops = new List<string>();
        var current = url;
        for (var i = 0; i < maxHops; i++)
        {
            hops.Add(current);
            var resp = await Get(current);
            if (resp.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect))
                return (resp, hops);
            var location = resp.Headers.Location!;
            if (Environment.GetEnvironmentVariable("SPIKE_TRACE") == "1") Console.WriteLine($"   hop {i}: {(int)resp.StatusCode} {current.Split('?')[0]} -> Location raw='{string.Join("|", resp.Headers.GetValues("Location"))}' abs={location.IsAbsoluteUri}");
            current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
        }
        throw new InvalidOperationException("redirect loop: " + string.Join(" -> ", hops));
    }
}
