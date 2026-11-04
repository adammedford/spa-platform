using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The local stand-in for the internal ALB, as Chromium sees it: an HTTPS listener on loopback that
/// terminates TLS with a certificate for the app domain and hands each request to the gateway through
/// the harness's <see cref="LoadBalancerTransport"/>, which sets the forwarding headers the ALB sets.
///
/// <para>Why a real listener and not a Playwright route. A routed request is paused before Chromium's
/// network stack adds <c>Sec-Fetch-*</c> and <c>Origin</c>, so the gateway saw a login navigation with
/// no Fetch Metadata and refused it (observed on Chromium 153 while building this suite). Everything
/// the deployed tier is for (Fetch Metadata, the <c>__Host-</c> prefix, <c>SameSite</c>, the content
/// security policy) is applied in that stack, so the browser must make real requests.</para>
///
/// <para>Chromium resolves every app host to this listener through <c>--host-resolver-rules</c>, as
/// the tunnel and the hosts file do in the deployed tier, and trusts its certificate because the
/// context ignores certificate errors, as a local certificate authority does there
/// (docs/plan/05-infrastructure-and-cost.md). The origin is still <c>https</c>, which is what the
/// prefix and <c>Secure</c> rules test.</para>
///
/// <para>Every exchange is kept, body included, so F-05 and N-23 search exactly the bytes the browser
/// received. Nothing here writes a log line.</para>
/// </summary>
public sealed class TlsLoadBalancer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpMessageInvoker transport;
    private readonly List<Exchange> exchanges = [];
    private HttpClient? raw;
    private volatile Func<Uri, bool>? hold;
    private readonly List<Uri> held = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> foreign = new(StringComparer.OrdinalIgnoreCase);

    private TlsLoadBalancer(WebApplication app, HttpMessageInvoker transport)
    {
        this.app = app;
        this.transport = transport;
    }

    /// <summary>Where the listener is, e.g. <c>127.0.0.1:54321</c>, for Chromium's resolver rule.</summary>
    public string Endpoint { get; private set; } = "";

    /// <summary>The listener's certificate key, as Chromium's <c>--ignore-certificate-errors-spki-list</c> names one (<see cref="ForeignSites.Spki"/>).</summary>
    public string Spki { get; private set; } = "";

    /// <summary>Every exchange carried so far, oldest first.</summary>
    public IReadOnlyList<Exchange> Exchanges
    {
        get
        {
            lock (exchanges)
            {
                return [.. exchanges];
            }
        }
    }

    public static async Task<TlsLoadBalancer> StartAsync(Uri gateway, string domain, CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        X509Certificate2 certificate = SelfSigned(domain);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        WebApplication app = builder.Build();
        TlsLoadBalancer balancer = new(app, new HttpMessageInvoker(new LoadBalancerTransport(gateway)));
        app.Run(balancer.ForwardAsync);
        await app.StartAsync(ct);

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        balancer.Endpoint = new Uri(address).Authority;
        balancer.Spki = ForeignSites.SpkiOf(certificate);
        return balancer;
    }

    private async Task ForwardAsync(HttpContext context)
    {
        HttpRequest request = context.Request;

        // The URL the browser asked for: its own host and port, which is what the transport writes into
        // Host and X-Forwarded-Port, and the request target as it arrived. Request.Path is decoded, and a
        // path the browser sent with %2e in it would reach the gateway as another path (N-10).
        string target = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
            ?? $"{request.PathBase}{request.Path}{request.QueryString}";
        Uri url = new($"https://{request.Host}{target}");
        using HttpRequestMessage message = new(new HttpMethod(request.Method), url);

        if (foreign.TryGetValue(url.Authority, out string? page))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(page, context.RequestAborted);
            return;
        }

        if (hold is { } match && match(url))
        {
            lock (held)
            {
                held.Add(url);
            }

            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync($"<!doctype html><title>{HeldTitle}</title>", context.RequestAborted);
            return;
        }

        using MemoryStream requestBody = new();
        await request.Body.CopyToAsync(requestBody, context.RequestAborted);
        if (requestBody.Length > 0 || request.ContentLength is not null)
        {
            message.Content = new ByteArrayContent(requestBody.ToArray());
        }

        Dictionary<string, string> sent = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, Microsoft.Extensions.Primitives.StringValues values) in request.Headers)
        {
            sent[name] = values.ToString();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.StartsWith(':'))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        message.Headers.Host = request.Host.Value;

        using HttpResponseMessage response = await transport.SendAsync(message, context.RequestAborted);
        byte[] body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;
        List<KeyValuePair<string, string>> received = [];
        foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] list = [.. values];
            context.Response.Headers.Append(name, list);
            received.AddRange(list.Select(value => KeyValuePair.Create(name, value)));
        }

        lock (exchanges)
        {
            exchanges.Add(new Exchange(request.Method, url, sent, (int)response.StatusCode, received, body));
        }

        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// Answers every request whose URL matches, instead of passing it to the gateway, with a page that
    /// says it was held, until <see cref="Release"/>. This is how a test stops a login or a logout part
    /// way: a Playwright route cannot, because it sees only the first request of a redirect chain
    /// (https://playwright.dev/dotnet/docs/api/class-browsercontext#browser-context-route), and every
    /// hop of a login after the first is a redirect.
    /// </summary>
    public void Hold(Func<Uri, bool> match) => hold = match;

    /// <summary>
    /// Answers every request for <paramref name="origin"/> with <paramref name="html"/>, as a page some
    /// other party serves: a hostile sibling under the app domain, or a site elsewhere. It is served from
    /// here, on loopback like the apps, rather than fulfilled by a Playwright route: a fetch from a
    /// route-fulfilled page to an app host never left Chromium 153 while this suite was built, most
    /// likely refused by its local network access rules, which treat a page with no address of its own
    /// as public (https://developer.chrome.com/blog/local-network-access). That would hide the gateway's
    /// answer behind the browser's.
    /// </summary>
    public void ServeForeign(Uri origin, string html) => foreign[origin.Authority] = html;

    /// <summary>Passes every request to the gateway again.</summary>
    public void Release() => hold = null;

    /// <summary>The URLs answered by a <see cref="Hold"/>, oldest first.</summary>
    public IReadOnlyList<Uri> Held
    {
        get
        {
            lock (held)
            {
                return [.. held];
            }
        }
    }

    /// <summary>The title of the page a held request is answered with.</summary>
    public const string HeldTitle = "held by the test";

    /// <summary>
    /// Sends a request to this listener over TLS the way a raw HTTP client in the deployed tier does
    /// through the tunnel: whatever the URL's host, the connection is to this listener, and the request
    /// carries exactly the headers given (no cookies, no Fetch Metadata, no redirects followed).
    /// </summary>
    public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
    {
        // The certificate is this run's own, trusted by no store, so the client trusts its key and no
        // other, as the browser context's SPKI list does.
        raw ??= RawClient.To(Endpoint, PinnedTo(Spki));
        return raw.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A validation that accepts the certificate with this key (<see cref="ForeignSites.SpkiOf"/>) and
    /// no other: for this run's own, which no store trusts. It pins rather than accepting any
    /// certificate, so a client given it validates against the stack's own certificate (CA5359; #589-Q5).
    /// </summary>
    public static System.Net.Security.RemoteCertificateValidationCallback PinnedTo(string spki) =>
        (_, certificate, _, _) => certificate is X509Certificate2 presented && ForeignSites.SpkiOf(presented) == spki;

    /// <summary>A certificate for <c>*.{domain}</c>, made for this run and trusted by no one but the test context.</summary>
    internal static X509Certificate2 SelfSigned(string domain, params string[] alsoFor)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN=*.{domain}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("*." + domain);
        foreach (string name in alsoFor)
        {
            names.AddDnsName(name);
        }
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        // An ephemeral key is not usable by SslStream on macOS; a PKCS#12 round trip gives it a persisted one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        transport.Dispose();
        raw?.Dispose();
    }
}

/// <summary>One request the browser made to the cell and the response it received, body included.</summary>
public sealed record Exchange(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> RequestHeaders,
    int Status,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body)
{
    /// <summary>A response header's values joined with ", ", or null.</summary>
    public string? Header(string name) =>
        Headers.Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value).ToArray() is { Length: > 0 } values
            ? string.Join(", ", values)
            : null;

    /// <summary>A request header as the browser sent it, or null.</summary>
    public string? Sent(string name) => RequestHeaders.TryGetValue(name, out string? value) ? value : null;

    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);

    /// <summary>What an assertion over the exchanges prints for this one: the URL with no query (<see cref="Redacted"/>).</summary>
    public override string ToString() => $"{Method} {Redacted.Url(Url)} -> {Status}";
}
