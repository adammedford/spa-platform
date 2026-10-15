using System.Net.Sockets;
using System.Text;
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
/// Workstream 4's gateway cut down to stage 6, <c>UsePlatformRequestGate</c>, with what it stands on:
/// stage 1 trusting loopback, so a test sends <c>X-Forwarded-Proto: https</c> the way the ALB does,
/// and a stand-in for stage 4 that pins <see cref="App"/> as <see cref="IAppFeature"/>, which is what
/// task 2.3's host resolution will do. The composed gateway cannot show stage 6 until task 2.3 lands
/// (<c>contracts/pipeline.md</c>), so the gate is proven here and again by workstream 6.
///
/// The terminal stands for everything after the gate. It answers 200 with <c>passed</c>, and it sends
/// <c>Access-Control-Allow-Origin: *</c> and a <c>Set-Cookie</c>, the way a careless upstream or a
/// session refresh might: a test that asserts a refusal carries neither can then fail if the gate let
/// the request through.
/// </summary>
public sealed class GateHost : IAsyncDisposable
{
    /// <summary>The app stage 4 pins. Its origin, behind the ALB, is <see cref="AppOrigin"/>.</summary>
    public static readonly AppDefinition App = TestApps.Entry("app1");

    public static readonly string AppOrigin = "https://" + App.Host;

    /// <summary>What a sibling SPA's page would send: another host under the same suffix.</summary>
    public static readonly string SiblingOrigin = "https://" + TestApps.Entry("app2").Host;

    public const string Passed = "passed";

    private readonly WebApplication app;
    private readonly HttpClient client;

    private GateHost(
        WebApplication app, Uri baseAddress, List<string> log, List<SettableRequestTelemetryFeature> telemetryFeatures)
    {
        this.app = app;
        BaseAddress = baseAddress;
        Log = log;
        this.telemetryFeatures = telemetryFeatures;

        // No cookie container: a test writes the Cookie header itself, case variants and all.
        client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    public Uri BaseAddress { get; }

    /// <summary>Every log line the host wrote, formatted.</summary>
    public List<string> Log { get; }

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, in the order the
    /// requests entered the telemetry middleware, with null for a request that left none. Empty unless
    /// the host was started with <c>telemetry: true</c>, because stage 2 is the only thing that creates
    /// that feature and this host is cut down to one stage.
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

    /// <summary>How many requests reached the terminal, which stands for every stage after the gate.</summary>
    public int Reached => reached;

    private int reached;

    private readonly List<SettableRequestTelemetryFeature> telemetryFeatures;

    /// <param name="pinApp">false leaves out stage 4's stand-in, so the gate finds no app.</param>
    /// <param name="telemetry">
    /// true installs stage 2's stand-in, a <see cref="SettableRequestTelemetryFeature"/> per request,
    /// and records what each request left on it in <see cref="TelemetryRules"/>.
    /// </param>
    public static async Task<GateHost> StartAsync(CancellationToken ct, bool pinApp = true, bool telemetry = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        List<string> log = [];
        builder.Logging.AddProvider(new ListLoggerProvider(log));
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "127.0.0.0/8")]);

        builder.AddPlatformSecurity();

        WebApplication web = builder.Build();
        GateHost? self = null;
        List<SettableRequestTelemetryFeature> telemetryFeatures = [];

        web.UsePlatformForwardedHeaders();

        // Stage 2's stand-in, when a test asks for it: the feature stage 2 sets on the request before
        // calling the rest of the pipeline and reads back once it returns
        // (contracts/telemetry.md -> "How `rule` and `session_ref` reach the request line"). It is off
        // by default, so every other test here runs the stage with no feature installed, which is what
        // holds the stage's write to being null-conditional.
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

        web.Use((context, next) =>
        {
            if (pinApp)
            {
                context.Features.Set<IAppFeature>(SettableAppFeature.For(App));
            }

            return next(context);
        });
        web.UsePlatformRequestGate();
        web.Run(context =>
        {
            Interlocked.Increment(ref self!.reached);
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.SetCookie = "__Host-spa-session=; Path=/; Secure; HttpOnly; Max-Age=0";
            return context.Response.WriteAsync(Passed, ct);
        });

        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        self = new GateHost(web, address, log, telemetryFeatures);
        return self;
    }

    /// <summary>
    /// A request as the ALB forwards a browser's: <c>X-Forwarded-Proto: https</c>, plus the headers
    /// given, written as given. A header whose value is null is left out.
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        string method, string path, CancellationToken ct, params (string Name, string? Value)[] headers)
    {
        HttpRequestMessage request = new(new HttpMethod(method), new Uri(BaseAddress, path));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        foreach ((string name, string? value) in headers)
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return client.SendAsync(request, ct);
    }

    /// <summary>
    /// Sends a request line and headers as written, over a raw socket, and returns what came back.
    /// Raw because <see cref="Uri"/> removes dot segments and decodes some escapes before a request
    /// leaves <see cref="HttpClient"/>, and the paths the gate refuses are exactly those.
    /// </summary>
    public async Task<string> RawAsync(string requestLine, CancellationToken ct, string extraHeaders = "")
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(BaseAddress.Host, BaseAddress.Port, ct);
        NetworkStream stream = tcp.GetStream();

        string head = $"{requestLine}\r\nHost: {BaseAddress.Authority}\r\nX-Forwarded-Proto: https\r\n"
            + extraHeaders + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);

        using MemoryStream received = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    /// <summary>The log lines that name <paramref name="rule"/>.</summary>
    public IReadOnlyList<string> LinesFor(string rule)
    {
        lock (Log)
        {
            return [.. Log.Where(line => line.Contains("rule=" + rule, StringComparison.Ordinal))];
        }
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
