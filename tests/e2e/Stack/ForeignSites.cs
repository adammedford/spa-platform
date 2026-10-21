using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Pages some other party serves, for the deployed stack (#211): a hostile sibling under the app domain
/// and a site elsewhere, on an HTTPS listener on loopback. The local stack serves them from its stand-in
/// load balancer (<see cref="TlsLoadBalancer.ServeForeign"/>); the cell's load balancer would send a
/// sibling to the gateway, which knows no such host.
///
/// <para>Served from loopback, not fulfilled inside the browser: a page with no address of its own
/// gets no request to a loopback host out of Chromium (<see cref="TlsLoadBalancer.ServeForeign"/>), and
/// through the tunnel every app host is loopback. Chromium trusts this listener by its key alone,
/// through <c>--ignore-certificate-errors-spki-list</c>
/// (https://source.chromium.org/chromium/chromium/src/+/main:services/network/public/cpp/network_switches.cc),
/// so the cell's own certificate is still checked against the workstation's authority.</para>
/// </summary>
public sealed class ForeignSites : IAsyncDisposable
{
    /// <summary>The label of the sibling host under the app domain: no app has it.</summary>
    public const string SiblingLabel = "evil";

    /// <summary>A site that is not under the app domain. <c>.example</c> is reserved (RFC 2606).</summary>
    public static readonly Uri Elsewhere = new($"https://{LocalStack.ForeignSite}/");

    private readonly WebApplication app;
    private readonly string domain;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> pages = new(StringComparer.OrdinalIgnoreCase);

    private ForeignSites(WebApplication app, string domain)
    {
        this.app = app;
        this.domain = domain;
    }

    /// <summary>Where the listener is, e.g. <c>127.0.0.1:54321</c>.</summary>
    public string Endpoint { get; private set; } = "";

    /// <summary>The base64 SHA-256 of the listener's certificate key, as Chromium's switch names one.</summary>
    public string Spki { get; private set; } = "";

    /// <summary>
    /// The resolver rules that send the sibling and the site elsewhere here. They go before any rule
    /// for <c>*.{domain}</c>: Chromium applies the first rule that matches.
    /// </summary>
    public string ResolverRules => $"MAP {SiblingLabel}.{domain} {Endpoint}, MAP {Elsewhere.Host} {Endpoint}";

    /// <summary>The sibling's origin on <paramref name="port"/>, the app hosts' port, so only the host differs from an app's.</summary>
    public Uri Sibling(int port) => new($"https://{SiblingLabel}.{domain}:{port}/");

    public static async Task<ForeignSites> StartAsync(string domain, CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        X509Certificate2 certificate = TlsLoadBalancer.SelfSigned(domain, Elsewhere.Host);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));

        WebApplication app = builder.Build();
        ForeignSites sites = new(app, domain);
        app.Run(sites.ServeAsync);
        await app.StartAsync(ct);

        sites.Endpoint = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Authority;
        sites.Spki = SpkiOf(certificate);
        return sites;
    }

    /// <summary>Answers every request for <paramref name="origin"/> with <paramref name="html"/>.</summary>
    public void Serve(Uri origin, string html) => pages[origin.Host] = html;

    /// <summary>Chromium's switch trusting certificates with these keys and no others beyond its store.</summary>
    public static string TrustSwitch(params string[] spkis) => "--ignore-certificate-errors-spki-list=" + string.Join(",", spkis);

    /// <summary>The base64 SHA-256 of a certificate's SubjectPublicKeyInfo.</summary>
    public static string SpkiOf(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private async Task ServeAsync(HttpContext context)
    {
        if (!pages.TryGetValue(context.Request.Host.Host, out string? html))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = "text/html";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
