using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Telemetry.Tests;

/// <summary>
/// Workstream 6's own gateway, cut down to stage 2 and what stands either side of it: the
/// telemetry entry points, a stand-in for stage 4 that pins <c>app1</c> as <see cref="IAppFeature"/>
/// (task 2.3's job; the composed gateway cannot run past its stub yet, contracts/pipeline.md), a
/// stand-in for the later stages that write <see cref="IRequestTelemetryFeature"/>, the framework's
/// authentication with one real OpenID Connect scheme, and a terminal that calls an upstream over a
/// real socket with the request's own query string.
///
/// Every log line the host writes is captured as the gateway's own JSON formatter renders it, so a
/// test reads the same bytes stdout would carry. The console logger is still registered, so the
/// lines also reach the test output.
///
/// <paramref name="telemetry"/> false is the control: the same host and the same traffic with no
/// telemetry entry points, its lines captured as each logger's own formatted message. It is what
/// shows the canaries do reach the logging pipeline, so N-16's "none appear" can fail.
/// </summary>
public sealed class TelemetryHost : IAsyncDisposable
{
    public const string AppId = "app1";

    /// <summary>The session cookie the later-stage stand-in reads its session key from.</summary>
    public const string SessionCookie = "__Host-spa-session";

    /// <summary>A header naming the rule id the later-stage stand-in rejects the request under.</summary>
    public const string RejectHeader = "X-Test-Reject";

    private readonly WebApplication app;
    private readonly EchoUpstreamListener upstream;

    private TelemetryHost(WebApplication app, EchoUpstream echo, EchoUpstreamListener upstream, Uri baseAddress,
        CapturedLog log, List<Activity> spans, List<Metric> metrics)
    {
        this.app = app;
        this.upstream = upstream;
        Upstream = echo;
        BaseAddress = baseAddress;
        Log = log;
        Spans = spans;
        Metrics = metrics;
    }

    public Uri BaseAddress { get; }

    public CapturedLog Log { get; }

    public List<Activity> Spans { get; }

    public List<Metric> Metrics { get; }

    /// <summary>The upstream's recorder, which saw what the terminal sent.</summary>
    public EchoUpstream Upstream { get; }

    public IServiceProvider Services => app.Services;

    public static async Task<TelemetryHost> StartAsync(
        CancellationToken ct,
        bool telemetry = true,
        IDictionary<string, string?>? configuration = null)
    {
        EchoUpstream echo = new();
        EchoUpstreamListener listener = await echo.ListenAsync(ct);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Cell"] = "test-cell",
        });
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        List<Activity> spans = [];
        List<Metric> metrics = [];
        CapturedLog log = new();

        if (telemetry)
        {
            builder.AddPlatformTelemetry();
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(spans));
            builder.Services.ConfigureOpenTelemetryMeterProvider(meters => meters.AddInMemoryExporter(metrics));
            builder.Logging.Services.AddSingleton<ILoggerProvider>(services => new CapturingProvider(log,
                services.GetServices<ConsoleFormatter>().Single(f => f.Name == PlatformJsonFormatter.FormatterName)));
        }
        else
        {
            builder.Logging.ClearProviders();
            builder.Logging.Services.AddSingleton<ILoggerProvider>(new CapturingProvider(log, formatter: null));
        }

        builder.Services.AddHttpClient("upstream", client => client.BaseAddress = listener.BaseAddress);

        // One real OpenID Connect scheme, so the callback below reaches the shipped handler, which
        // logs the full callback URL at Trace (ADR-0007 -> Logging rules this forces). The
        // configuration is static, so nothing is fetched.
        builder.Services.AddAuthentication()
            .AddCookie("cookie:" + AppId)
            .AddOpenIdConnect("oidc:" + AppId, options =>
            {
                options.SignInScheme = "cookie:" + AppId;
                options.ClientId = "client-" + AppId;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.invalid",
                    AuthorizationEndpoint = "https://idp.invalid/authorize",
                    TokenEndpoint = "https://idp.invalid/token",
                };
            });

        WebApplication web = builder.Build();

        if (telemetry)
        {
            web.UsePlatformTelemetry();
        }

        // Stand-in for stage 4: app1 on every host except unknown.invalid, which gets the 404 and
        // the host.unknown rule id the real stage gives an unknown host (change request 6.4 ->
        // Decision). Written null-conditionally, as every real stage writes it.
        AppDefinition entry = TestApps.Entry(AppId);
        web.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Unknown-Host"].Count > 0)
            {
                context.Features.Get<IRequestTelemetryFeature>()?.Rule = "host.unknown";
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Features.Set<IAppFeature>(SettableAppFeature.For(entry));
            await next(context);
        });

        // Stand-in for the later stages. Workstream 3 turns the session cookie's key into its
        // reference, and the key goes no further; a request gate rejects under the rule id the test
        // names, with the 403 the gate answers.
        web.Use(async (context, next) =>
        {
            IRequestTelemetryFeature? fields = context.Features.Get<IRequestTelemetryFeature>();
            if (context.Request.Cookies.TryGetValue(SessionCookie, out string? key) && !string.IsNullOrEmpty(key))
            {
                fields?.SessionRef = SessionRef.Of(key);
            }

            if (context.Request.Headers[RejectHeader] is [{ } rule, ..])
            {
                fields?.Rule = rule;
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        web.UseAuthentication();

        web.Run(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                HttpClient client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("upstream");
                using HttpResponseMessage response = await client.GetAsync(
                    "/items" + context.Request.QueryString.Value, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                return;
            }

            // /status/{code} answers with that code, so a test can pick out its own requests among
            // instruments that every host in the test process reports to.
            context.Response.StatusCode = context.Request.Path.StartsWithSegments("/status", out PathString code)
                ? int.Parse(code.Value!.TrimStart('/'), System.Globalization.CultureInfo.InvariantCulture)
                : StatusCodes.Status200OK;
        });

        await web.StartAsync(ct);

        Uri address = new(web.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the host started without an address."));

        return new TelemetryHost(web, echo, listener, address, log, spans, metrics);
    }

    /// <summary>Exports what the providers hold, so spans and metrics are in the lists.</summary>
    public void Flush()
    {
        app.Services.GetService<TracerProvider>()?.ForceFlush();
        app.Services.GetService<MeterProvider>()?.ForceFlush();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await upstream.DisposeAsync();
    }

    /// <summary>Every line written, and those that are JSON parsed for assertions.</summary>
    public sealed class CapturedLog
    {
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines)
                {
                    return [.. lines];
                }
            }
        }

        public IReadOnlyList<JsonElement> Json =>
            [.. Lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())];

        internal void Add(string line)
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }
    }

    /// <summary>
    /// A logger provider that renders each entry with the gateway's own formatter, the one the
    /// console logger uses, into a line of its own. With no formatter (the control), it keeps the
    /// logger's own formatted message.
    /// </summary>
    private sealed class CapturingProvider(CapturedLog log, ConsoleFormatter? formatter) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(log, formatter, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturedLog log, ConsoleFormatter? formatter, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> render)
            {
                if (formatter is null)
                {
                    log.Add(render(state, exception) + " " + exception);
                    return;
                }

                using StringWriter writer = new();
                formatter.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, render),
                    new LoggerExternalScopeProvider(), writer);
                foreach (string line in writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    log.Add(line);
                }
            }
        }
    }
}
