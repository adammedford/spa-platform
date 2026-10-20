using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
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
/// Workstream 4's gateway cut down to <c>UsePlatformAntiforgery</c>, with stand-ins for what it
/// stands on and for the two endpoints that call <see cref="ICsrfTokens"/>. The composed gateway cannot
/// show the antiforgery check until task 2.3 resolves a host (<c>contracts/pipeline.md</c>), so it is
/// proven here and again by workstream 6.
///
/// <list type="bullet">
/// <item><c>UsePlatformForwardedHeaders</c> trusts loopback, so a request carries
/// <c>X-Forwarded-Proto: https</c> the way the ALB sends it. Without it the framework's antiforgery
/// refuses to run at all (see <see cref="SendAsync"/>).</item>
/// <item>Host resolution's stand-in pins <c>app1</c> or <c>app2</c> by the <c>Host</c> header, as task
/// 2.3 will. Both apps are served by one host, so one data-protection key ring signs both apps' tokens,
/// as in the real gateway: a refusal across apps is then the app binding's doing, not a key mismatch
/// (R-E3b).</item>
/// <item>Authentication's stand-in signs in the user named in <c>X-Test-User</c>, with a <c>sub</c>
/// claim and an authentication type, because the framework binds a token to the user only when the
/// identity is authenticated, and then by its <c>sub</c> claim first
/// (https://github.com/dotnet/aspnetcore/blob/main/src/Antiforgery/src/Internal/DefaultClaimUidExtractor.cs).</item>
/// <item><c>GET /bff/session</c> stands for workstream 3's endpoint, calling <see cref="ICsrfTokens.Issue"/>,
/// and <c>POST /bff/logout</c> for logout, calling <see cref="ICsrfTokens.Clear"/>.</item>
/// <item>Every other request reaches a terminal standing for the proxy. It reports how many body bytes
/// had been read before it, then echoes the body, and it sends <c>Access-Control-Allow-Origin</c>, so a
/// refusal that carries none shows the request stopped at the antiforgery check.</item>
/// </list>
/// </summary>
public sealed class AntiforgeryHost : IAsyncDisposable
{
    public static readonly AppDefinition App1 = TestApps.Entry("app1");

    public static readonly AppDefinition App2 = TestApps.Entry("app2");

    public const string UserHeader = "X-Test-User";

    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly List<SettableRequestTelemetryFeature> telemetryFeatures;
    private int reached;
    private long lastBodyBytesRead = -1;

    private AntiforgeryHost(
        WebApplication app, Uri baseAddress, List<string> log, List<SettableRequestTelemetryFeature> telemetryFeatures)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Log = log;
        this.telemetryFeatures = telemetryFeatures;

        // No cookie container: each test plays a browser, or an attacker, and writes Cookie itself.
        client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    public Uri BaseAddress { get; }

    /// <summary>Every log line the host wrote, formatted.</summary>
    public List<string> Log { get; }

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered the telemetry middleware, with null for a request that left none. Empty unless
    /// the host was started with <c>telemetry: true</c>, because UsePlatformTelemetry is the only thing
    /// that creates that feature and this host is cut down to one stage.
    /// </summary>
    public IReadOnlyList<string?> TelemetryRules
    {
        get
        {
            lock (telemetryFeatures)
            {
                return [.. telemetryFeatures.Select(feature => feature.Rule)];
            }
        }
    }

    /// <summary>How many requests reached the terminal, which stands for everything after this stage.</summary>
    public int Reached => Volatile.Read(ref reached);

    /// <summary>How many bytes of the last request's body anything read before the terminal.</summary>
    public long LastBodyBytesReadBeforeTerminal => Interlocked.Read(ref lastBodyBytesRead);

    /// <param name="ct">The test's token.</param>
    /// <param name="configure">
    /// Runs after <c>AddPlatformSecurity</c>, for a test that puts back a framework default to show what
    /// the platform's setting prevents.
    /// </param>
    /// <param name="telemetry">
    /// true installs UsePlatformTelemetry's stand-in, a <see cref="SettableRequestTelemetryFeature"/>
    /// per request, and records what each request left on it in <see cref="TelemetryRules"/>.
    /// </param>
    public static async Task<AntiforgeryHost> StartAsync(
        CancellationToken ct, Action<IServiceCollection>? configure = null, bool telemetry = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        List<string> log = [];
        builder.Logging.AddProvider(new ListLoggerProvider(log));
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "127.0.0.0/8")]);

        builder.AddPlatformSecurity();

        // Keys in memory, so a test run writes no key ring under the user's profile.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        configure?.Invoke(builder.Services);

        WebApplication web = builder.Build();
        AntiforgeryHost? self = null;
        List<SettableRequestTelemetryFeature> telemetryFeatures = [];

        web.UsePlatformForwardedHeaders();

        // UsePlatformTelemetry's stand-in, when a test asks for it: the feature that stage sets on the
        // request before calling the rest of the pipeline and reads back once it returns
        // (contracts/telemetry.md -> "How `rule` and `session_ref` reach the request line"). It is off
        // by default, so every other test here runs the stage with no feature installed; the write is
        // Rejection's and null-conditional, proven in tests/Platform.Abstractions.Tests (#142, Q10).
        //
        // The feature is recorded before the request runs rather than after: each stage sets the rule
        // id before it writes the response, so a test reading the recorded feature cannot race the
        // response body back to the client.
        if (telemetry)
        {
            web.Use((context, next) =>
            {
                SettableRequestTelemetryFeature feature = new();
                lock (telemetryFeatures)
                {
                    telemetryFeatures.Add(feature);
                }

                context.Features.Set<IRequestTelemetryFeature>(feature);
                return next(context);
            });
        }

        // Host resolution's stand-in: the app whose host the request names. Any other host is a test
        // error.
        web.Use((context, next) =>
        {
            AppDefinition pinned = context.Request.Host.Host switch
            {
                var h when h == App1.Host => App1,
                var h when h == App2.Host => App2,
                var h => throw new InvalidOperationException($"The test host serves {App1.Host} and {App2.Host}, not {h}."),
            };
            context.Features.Set<IAppFeature>(SettableAppFeature.For(pinned));
            return next(context);
        });

        // Authentication's stand-in: the signed-in user, if the test names one.
        web.Use((context, next) =>
        {
            string? user = context.Request.Headers[UserHeader];
            if (user is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], authenticationType: "test"));
            }

            return next(context);
        });

        // Counts what anything after this point reads of the body, so a test can show the antiforgery
        // check read none.
        web.Use(async (context, next) =>
        {
            CountingStream counting = new(context.Request.Body);
            context.Request.Body = counting;
            context.Items[typeof(CountingStream)] = counting;
            Interlocked.Exchange(ref self!.lastBodyBytesRead, -1);
            await next(context);
            if (context.Items.ContainsKey("terminal") is false)
            {
                Interlocked.Exchange(ref self.lastBodyBytesRead, counting.BytesRead);
            }
        });

        web.UsePlatformAntiforgery();

        web.Run(async context =>
        {
            ICsrfTokens tokens = context.RequestServices.GetRequiredService<ICsrfTokens>();

            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/bff/session")
            {
                await context.Response.WriteAsJsonAsync(new { csrfToken = tokens.Issue(context) }, ct);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/bff/logout")
            {
                tokens.Clear(context);
                return;
            }

            CountingStream counting = (CountingStream)context.Items[typeof(CountingStream)]!;
            context.Items["terminal"] = true;
            Interlocked.Exchange(ref self!.lastBodyBytesRead, counting.BytesRead);
            Interlocked.Increment(ref self.reached);

            using StreamReader reader = new(context.Request.Body);
            string body = await reader.ReadToEndAsync(ct);
            context.Response.Headers.AccessControlAllowOrigin = "*";
            await context.Response.WriteAsync("passed:" + body, ct);
        });

        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        self = new AntiforgeryHost(web, address, log, telemetryFeatures);
        return self;
    }

    /// <summary>
    /// A request as the ALB forwards a browser's, to <paramref name="app"/>'s host, as
    /// <paramref name="user"/> (null for no session), carrying <paramref name="cookie"/> as the whole
    /// <c>Cookie</c> header and <paramref name="csrfHeader"/> as <c>X-CSRF-TOKEN</c>, each left out when
    /// null. <paramref name="https"/> false leaves out <c>X-Forwarded-Proto</c>, so the request arrives
    /// as plain HTTP.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        string method, string path, AppDefinition app, string? user, CancellationToken ct,
        string? cookie = null, string? csrfHeader = null, HttpContent? content = null, bool https = true)
    {
        HttpRequestMessage request = new(new HttpMethod(method), new Uri(BaseAddress, path)) { Content = content };
        request.Headers.Host = app.Host;
        if (https)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        }

        if (user is not null)
        {
            request.Headers.TryAddWithoutValidation(UserHeader, user);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (csrfHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfHeader);
        }

        return client.SendAsync(request, ct);
    }

    /// <summary>
    /// <c>GET /bff/session</c> as <paramref name="user"/> on <paramref name="app"/>: what the SPA does to get
    /// a token. Returns the request token and the cookie as the browser would send it back
    /// (<c>name=value</c>), or a null cookie when the browser already held one and none was set.
    /// </summary>
    public async Task<Issued> IssueAsync(AppDefinition app, string user, CancellationToken ct, string? cookie = null)
    {
        using HttpResponseMessage response = await SendAsync("GET", "/bff/session", app, user, ct, cookie: cookie);
        response.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        string token = body.RootElement.GetProperty("csrfToken").GetString()
            ?? throw new InvalidOperationException("no csrfToken in the session response.");
        string? setCookie = CsrfSetCookie(response);
        return new Issued(token, setCookie is null ? cookie : setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)], setCookie);
    }

    /// <summary>The <c>Set-Cookie</c> line for <c>__Host-spa-csrf</c>, or null when the response sets none.</summary>
    public static string? CsrfSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault(v => v.StartsWith("__Host-spa-csrf=", StringComparison.Ordinal))
            : null;

    /// <summary>The log lines that name <paramref name="rule"/>.</summary>
    public IReadOnlyList<string> LinesFor(string rule)
    {
        lock (Log)
        {
            return [.. Log.Where(line => line.Contains("rule=" + rule, StringComparison.Ordinal))];
        }
    }

    /// <summary>Whether any log line contains <paramref name="value"/>.</summary>
    public bool Logged(string value)
    {
        lock (Log)
        {
            return Log.Any(line => line.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>A form body carrying <paramref name="token"/> in the framework's default form field.</summary>
    public static FormUrlEncodedContent FormWithToken(string token) =>
        new([new KeyValuePair<string, string>(new AntiforgeryOptions().FormFieldName, token), new("order", "7")]);

    public static StringContent Json(string json) => new(json, new MediaTypeHeaderValue("application/json"));

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    /// <summary>A request token, and the antiforgery cookie as the browser would send it back.</summary>
    public sealed record Issued(string RequestToken, string? Cookie, string? SetCookie);

    private sealed class CountingStream(Stream inner) : Stream
    {
        private long bytesRead;

        public long BytesRead => Interlocked.Read(ref bytesRead);

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            Interlocked.Add(ref bytesRead, read);
            return read;
        }
    }
}
