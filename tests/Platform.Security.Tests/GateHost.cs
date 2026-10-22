using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Security.Tests;

/// <summary>
/// Workstream 4's gateway cut down to <c>UsePlatformRequestGate</c>, with what it stands on:
/// <c>UsePlatformForwardedHeaders</c> trusting loopback, so a test sends
/// <c>X-Forwarded-Proto: https</c> the way the ALB does, and a stand-in for host resolution that pins
/// <see cref="App"/> as <see cref="IAppFeature"/>, which is what task 2.3 will do. The composed
/// gateway cannot show the request gate until task 2.3 lands (<c>contracts/pipeline.md</c>), so the
/// gate is proven here and again by workstream 6.
///
/// The listener, the telemetry stand-in, the captured log, the raw sender and the client are
/// <see cref="StageHost"/>'s (#140); what is here is the stages, the pin and the terminal.
///
/// The terminal stands for everything after the gate. It answers 200 with <c>passed</c>, and it sends
/// <c>Access-Control-Allow-Origin: *</c> and a <c>Set-Cookie</c>, the way a careless upstream or a
/// session refresh might: a test that asserts a refusal carries neither can then fail if the gate let
/// the request through.
/// </summary>
public sealed class GateHost : IAsyncDisposable
{
    /// <summary>
    /// The app host resolution's stand-in pins. Its origin, behind the ALB, is <see cref="AppOrigin"/>.
    /// </summary>
    public static readonly AppDefinition App = TestApps.Entry("app1");

    public static readonly string AppOrigin = "https://" + App.Host;

    /// <summary>What a sibling SPA's page would send: another host under the same suffix.</summary>
    public static readonly string SiblingOrigin = "https://" + TestApps.Entry("app2").Host;

    public const string Passed = "passed";

    private StageHost stage = null!;
    private int reached;

    private GateHost()
    {
    }

    public Uri BaseAddress => stage.BaseAddress;

    /// <summary>Every line the host logged, captured at Trace.</summary>
    public CapturedLog Log => stage.Log;

    /// <summary>
    /// The rule id each request left on <see cref="IRequestTelemetryFeature"/>, null where none was left.
    /// Empty unless the host was started with <c>telemetryStandIn: true</c>.
    /// </summary>
    public IReadOnlyList<string?> TelemetryRules => stage.TelemetryRules;

    /// <summary>How many requests reached the terminal, which stands for every stage after the gate.</summary>
    public int Reached => Volatile.Read(ref reached);

    /// <param name="pinApp">false leaves out the host-resolution stand-in, so the gate finds no app.</param>
    /// <param name="telemetryStandIn">
    /// true asks <see cref="StageHost"/> for the telemetry stand-in, outermost as
    /// <c>UsePlatformTelemetry</c> is. Off, every stage here runs with no feature installed, and its
    /// write stays proven null-conditional (change request 6.4).
    /// </param>
    public static async Task<GateHost> StartAsync(CancellationToken ct, bool pinApp = true, bool telemetryStandIn = false)
    {
        GateHost self = new();
        self.stage = await StageHost.StartAsync(new StageHostOptions
        {
            TelemetryStandIn = telemetryStandIn,
            Services = builder =>
            {
                builder.Configuration.AddInMemoryCollection(
                    [new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "127.0.0.0/8")]);
                builder.AddPlatformSecurity();
            },
            Pipeline = web =>
            {
                web.UsePlatformForwardedHeaders();

                // Host resolution's stand-in: one app, whatever the host.
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
                    Interlocked.Increment(ref self.reached);
                    context.Response.Headers.AccessControlAllowOrigin = "*";
                    context.Response.Headers.SetCookie = "__Host-spa-session=; Path=/; Secure; HttpOnly; Max-Age=0";
                    return context.Response.WriteAsync(Passed, ct);
                });
            },
        }, ct);

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

        return stage.Client.SendAsync(request, ct);
    }

    /// <summary>
    /// <paramref name="method"/> on <paramref name="target"/> exactly as written, over a raw socket, as
    /// the ALB forwards it: <c>Host</c> is the listener's authority and <c>X-Forwarded-Proto</c> is
    /// https, then the headers given, in order and repeats kept. Raw because <see cref="Uri"/> removes
    /// dot segments and decodes some escapes before a request leaves <see cref="HttpClient"/>, and the
    /// paths the gate refuses are exactly those.
    /// </summary>
    public Task<RawResponse> RawAsync(string method, string target, CancellationToken ct, params (string Name, string Value)[] headers) =>
        stage.RawAsync(method, target, BaseAddress.Authority, [("X-Forwarded-Proto", "https"), .. headers], ct);

    public ValueTask DisposeAsync() => stage.DisposeAsync();
}
