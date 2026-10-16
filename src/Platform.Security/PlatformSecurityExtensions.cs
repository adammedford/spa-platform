using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Security;

/// <summary>
/// Workstream 4's security entry points in <c>contracts/pipeline.md</c>:
/// <c>UsePlatformForwardedHeaders</c>, <c>UsePlatformSecurityHeaders</c>, <c>UsePlatformRequestGate</c>
/// and <c>UsePlatformAntiforgery</c>.
///
/// <b>Stubs.</b> Task I-4 wrote these so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Tasks 4.3 to 4.6 fill them in. Keep these signatures and add no others.
///
/// The pipeline contract names the owner of each stage but not the project, and workstream 4 owns
/// two. The split is by concern: the request gate, the headers, the antiforgery check and the
/// forwarded-header and request limits are here, and the YARP configuration and route are in
/// <c>Platform.Proxy</c>.
/// </summary>
public static class PlatformSecurityExtensions
{
    /// <summary>
    /// The forwarded-header options <c>UsePlatformForwardedHeaders</c> applies, and Kestrel's
    /// request limits (task 4.6). The framework's antiforgery with the platform's options, the app
    /// binding and <see cref="Platform.Abstractions.ICsrfTokens"/>, which workstream 3 calls to issue
    /// the token and to clear it at logout (task 4.4).
    /// </summary>
    public static IHostApplicationBuilder AddPlatformSecurity(this IHostApplicationBuilder builder)
    {
        PlatformForwardedHeaders.Add(builder.Services);
        builder.Services.Configure<KestrelServerOptions>(PlatformSecurityLimits.Apply);
        PlatformAntiforgery.Add(builder.Services);
        return builder;
    }

    /// <summary>
    /// It runs straight after <c>UsePlatformTelemetry</c> and before everything else, for a reason:
    /// everything after it reads the scheme and the client address, and the antiforgery cookie's
    /// <c>SecurePolicy = Always</c> has to be applied after it, because a <c>__Host-</c> cookie sent
    /// without <c>Secure</c> is discarded (ADR-0007).
    ///
    /// <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> are trusted from
    /// <c>ForwardedHeaders:TrustedNetworks</c> only, and <c>X-Forwarded-Host</c> is never read.
    /// Task 4.6.
    ///
    /// The method allowlist runs here too, straight after, because this is the earliest stage
    /// workstream 4 owns and the one task 4.6 fills: a method no path accepts (<c>TRACE</c>,
    /// <c>CONNECT</c>) gets 405 under <c>gate.method</c> before any later stage sees it, and the
    /// telemetry stage ahead of it logs and counts the refusal (ADR-0018). Per-path methods are the
    /// request gate's.
    /// </summary>
    public static IApplicationBuilder UsePlatformForwardedHeaders(this IApplicationBuilder app)
    {
        PlatformForwardedHeaders.Use(app);

        ILogger logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(MethodAllowlist).FullName!);
        app.Use((context, next) => MethodAllowlist.InvokeAsync(context, next, logger));

        return app;
    }

    /// <summary>
    /// The app's content security policy and the fixed header set, on every response, overwriting any
    /// copy an upstream sent. Task 4.5.
    /// </summary>
    public static IApplicationBuilder UsePlatformSecurityHeaders(this IApplicationBuilder app)
    {
        SecurityHeaders headers = new(
            app.ApplicationServices.GetRequiredService<IConfiguration>(),
            app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SecurityHeaders).FullName!));
        app.Use((context, next) => headers.InvokeAsync(context, next));
        return app;
    }

    /// <summary>
    /// The rules in <c>contracts/request-gate.md</c>, one rule id per rejection. It runs before
    /// authentication, because no rule in it needs the user. Task 4.3.
    ///
    /// The session the contract asks of an asset request is <c>UsePlatformShellGate</c>'s, and the
    /// token on an unsafe <c>/api</c> method is <c>UsePlatformAntiforgery</c>'s: both need the user,
    /// which this stage runs before.
    /// </summary>
    public static IApplicationBuilder UsePlatformRequestGate(this IApplicationBuilder app)
    {
        RequestGate gate = new(app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RequestGate).FullName!));
        app.Use((context, next) => gate.InvokeAsync(context, next));
        return app;
    }

    /// <summary>
    /// Unsafe methods on <c>/api</c> need a valid <c>X-CSRF-TOKEN</c>. It runs after the request gate
    /// because the token check needs the user. Header tokens only, so a request body is never read
    /// (finding R-E7). Task 4.4.
    ///
    /// The framework's own <c>UseAntiforgery</c> is not called. It validates only endpoints that carry
    /// antiforgery metadata, such as a minimal API that binds a form, and it would then demand a token
    /// of a form-bound <c>/bff/logout</c>, which the contract says takes none. Every check this
    /// platform needs is the one below, by path and method.
    ///
    /// Leaving it out does not free such an endpoint: endpoint routing refuses to run one that carries
    /// antiforgery metadata when <c>UseAntiforgery</c> did not run ("contains anti-forgery metadata,
    /// but a middleware was not found that supports anti-forgery", an InvalidOperationException, so a
    /// 500). A form-bound <c>/bff/logout</c> therefore has to opt out with <c>DisableAntiforgery()</c>
    /// or read the form itself.
    /// https://learn.microsoft.com/aspnet/core/security/anti-request-forgery#antiforgery-with-minimal-apis
    /// </summary>
    public static IApplicationBuilder UsePlatformAntiforgery(this IApplicationBuilder app)
    {
        AntiforgeryStage stage = new(
            app.ApplicationServices.GetRequiredService<Platform.Abstractions.ICsrfTokens>(),
            app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AntiforgeryStage).FullName!));
        app.Use((context, next) => stage.InvokeAsync(context, next));
        return app;
    }
}
