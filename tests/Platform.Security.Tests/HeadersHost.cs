using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Security.Tests;

/// <summary>
/// Workstream 4's gateway cut down to <c>UsePlatformSecurityHeaders</c>, with what it stands
/// on and one real stage after it. The composed gateway cannot show the headers until task 2.3 resolves
/// a host (<c>contracts/pipeline.md</c>), so it is proven here and again by workstream 6.
///
/// <list type="bullet">
/// <item><c>UsePlatformForwardedHeaders</c> trusts loopback, so a request carries
/// <c>X-Forwarded-Proto: https</c> as the ALB sends it.</item>
/// <item>Host resolution's stand-in pins the app whose <c>host</c> equals the request's <c>Host</c>
/// header, as task 2.3 will, so one host serves several apps with different <c>csp</c> additions
/// (F-06).</item>
/// <item><c>UsePlatformRequestGate</c>, the real one, follows the headers stage. A refusal it writes is
/// an API error written by a later stage, which is the response N-24 most needs the headers stage to
/// cover.</item>
/// <item>The terminal stands for the endpoints: an HTML shell, an asset, <c>/bff/session</c> and a
/// proxied API response. Every one of them first sets its own copy of each header the headers stage
/// owns, plus two <c>Access-Control-*</c> headers, as a careless upstream would. An overwrite that
/// failed would then show as a second value or the wrong one.</item>
/// </list>
/// </summary>
public sealed class HeadersHost : IAsyncDisposable
{
    /// <summary>The provider origin configured for issuer <c>primary</c>, the issuer <see cref="TestApps"/> uses.</summary>
    public const string BrowserOrigin = "https://login.idp.test";

    /// <summary>What the terminal sends as its own copy of every header the headers stage owns.</summary>
    public static readonly IReadOnlyDictionary<string, string> UpstreamCopies = new Dictionary<string, string>
    {
        ["Content-Security-Policy"] = "default-src *",
        ["X-Content-Type-Options"] = "sniff-away",
        ["Referrer-Policy"] = "unsafe-url",
        ["Cross-Origin-Opener-Policy"] = "unsafe-none",
        ["Cross-Origin-Resource-Policy"] = "cross-origin",
        ["Strict-Transport-Security"] = "max-age=0",
        ["Access-Control-Allow-Origin"] = "*",
        ["Access-Control-Allow-Credentials"] = "true",
    };

    private readonly WebApplication app;
    private readonly HttpClient client;

    private HeadersHost(WebApplication app, Uri baseAddress, List<string> log)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Log = log;
        client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    public Uri BaseAddress { get; }

    /// <summary>Every log line the host wrote, formatted.</summary>
    public List<string> Log { get; }

    /// <param name="ct">The test's token.</param>
    /// <param name="apps">The registry the host-resolution stand-in reads, by <c>host</c>.</param>
    /// <param name="issuers">
    /// <c>Issuers:{name}:BrowserOrigin</c> settings. Null configures <c>primary</c> as
    /// <see cref="BrowserOrigin"/>.
    /// </param>
    public static async Task<HeadersHost> StartAsync(
        CancellationToken ct, IEnumerable<AppDefinition> apps, IDictionary<string, string>? issuers = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        List<string> log = [];
        builder.Logging.AddProvider(new ListLoggerProvider(log));

        List<KeyValuePair<string, string?>> settings = [new("ForwardedHeaders:TrustedNetworks:0", "127.0.0.0/8")];
        foreach ((string name, string origin) in issuers ?? new Dictionary<string, string> { ["primary"] = BrowserOrigin })
        {
            settings.Add(new($"Issuers:{name}:BrowserOrigin", origin));
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPlatformSecurity();

        Dictionary<string, AppDefinition> byHost = apps.ToDictionary(a => a.Host, StringComparer.Ordinal);

        WebApplication web = builder.Build();
        web.UsePlatformForwardedHeaders();
        web.Use((context, next) =>
        {
            if (byHost.TryGetValue(context.Request.Headers.Host.ToString(), out AppDefinition? pinned))
            {
                context.Features.Set<IAppFeature>(SettableAppFeature.For(pinned));
            }

            return next(context);
        });
        web.UsePlatformSecurityHeaders();
        web.UsePlatformRequestGate();
        web.Run(context =>
        {
            foreach ((string name, string value) in UpstreamCopies)
            {
                context.Response.Headers[name] = value;
            }

            string path = context.Request.Path.Value ?? "/";
            (string type, string body) = path switch
            {
                "/" => ("text/html", "<!doctype html><title>shell</title>"),
                "/bff/session" => ("application/json", "{\"sub\":\"alice\"}"),
                _ when path.StartsWith("/api/", StringComparison.Ordinal) => ("application/json", "{\"upstream\":true}"),
                _ => ("text/javascript", "console.log(1);"),
            };
            context.Response.ContentType = type;
            return context.Response.WriteAsync(body, ct);
        });

        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        return new HeadersHost(web, address, log);
    }

    /// <summary>
    /// A request to <paramref name="app"/>'s host as the ALB forwards a browser's: the <c>Host</c> header
    /// is the app's, <c>X-Forwarded-Proto</c> is https, and the headers given are added as written.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        AppDefinition app, string method, string path, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        HttpRequestMessage request = new(new HttpMethod(method), new Uri(BaseAddress, path));
        request.Headers.Host = app.Host;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return client.SendAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
