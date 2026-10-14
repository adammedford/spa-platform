using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// Stage 9 and the token pair behind it: task 4.4, ADR-0007, test N-06 and evidence rows R-E1 to R-E7
/// (<c>docs/plan/07-evidence.md</c>). The framework's antiforgery does the cryptography; this file
/// sets five of its options, binds the app into the token and decides which requests need one.
/// </summary>
internal static class PlatformAntiforgery
{
    /// <summary><c>contracts/http-contract.md</c> -> Cookies.</summary>
    internal const string CookieName = "__Host-spa-csrf";

    /// <summary><c>contracts/http-contract.md</c> -> Request headers the SPA sends.</summary>
    internal const string HeaderName = "X-CSRF-TOKEN";

    internal static void Add(IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = CookieName;
            options.HeaderName = HeaderName;

            // R-E5: the default policy, SameAsRequest, sends the cookie without Secure on a request that
            // arrived as http, and a browser discards a __Host- cookie without Secure. Always also makes
            // every IAntiforgery call throw on a request whose scheme is not https
            // (DefaultAntiforgery.CheckSSLConfig), which is why stage 1 has to have applied
            // X-Forwarded-Proto first (contracts/pipeline.md).
            // https://learn.microsoft.com/aspnet/core/security/anti-request-forgery#antiforgery-options
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;

            // R-E7: with this off, a request that has no header token has its form body read in search
            // of one, and the gateway would consume a body it is about to proxy. R-E6 is the proof.
            options.SuppressReadingTokenFromFormBody = true;

            // The framework adds X-Frame-Options: SAMEORIGIN to the response that issues a token. The
            // contract's header set has no X-Frame-Options: framing is stage 5's frame-ancestors 'none',
            // which is stricter (contracts/http-contract.md -> Response headers).
            options.SuppressXFrameOptionsHeader = true;
        });

        // R-E3: without this, app 1's pair validates on app 2 for the same user. R-E3b is the proof.
        // Replace, not add: AddAntiforgery registers the framework's empty provider.
        services.Replace(ServiceDescriptor.Singleton<IAntiforgeryAdditionalDataProvider, AppBinding>());
        services.AddSingleton<ICsrfTokens, CsrfTokens>();
    }

    /// <summary>
    /// The app the request was resolved to, read from <see cref="IAppFeature"/> (stage 4). A missing
    /// feature is a misordered pipeline, and it throws rather than binding every token to the same
    /// empty value, which would undo the binding.
    /// </summary>
    private static string AppIdOf(HttpContext context) =>
        context.Features.Get<IAppFeature>()?.App.AppId
            ?? throw new InvalidOperationException(
                "Antiforgery (stage 9) runs after host resolution (stage 4), which sets IAppFeature.");

    /// <summary>Binds the app id into the request token, so one app's pair is refused on another.</summary>
    private sealed class AppBinding : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context) => AppIdOf(context);

        public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
            string.Equals(additionalData, AppIdOf(context), StringComparison.Ordinal);
    }

    /// <summary>The <see cref="ICsrfTokens"/> workstream 3 calls from <c>/bff/session</c> and logout.</summary>
    private sealed class CsrfTokens(IAntiforgery antiforgery, IOptions<AntiforgeryOptions> options) : ICsrfTokens
    {
        public string Issue(HttpContext context) =>
            // RequestToken is null only when the framework was asked for the cookie token alone, which
            // GetAndStoreTokens never is.
            antiforgery.GetAndStoreTokens(context).RequestToken
                ?? throw new InvalidOperationException("The framework issued no request token.");

        public Task<bool> IsValidAsync(HttpContext context) =>
            // Header only: with SuppressReadingTokenFromFormBody the token store never reads the form
            // (R-E6). The framework answers true for GET, HEAD, OPTIONS and TRACE itself, and turns a
            // missing or mismatched token into false rather than an exception; it throws only on a
            // request that is not https, which is a misconfigured stage 1 and should be loud. TRACE
            // never gets here: stage 1's method allowlist answers it 405 (N-20).
            // https://github.com/dotnet/aspnetcore/blob/main/src/Antiforgery/src/Internal/DefaultAntiforgery.cs
            antiforgery.IsRequestValidAsync(context);

        public void Clear(HttpContext context) =>
            // IAntiforgery has no way to remove its cookie. A browser deletes a __Host- cookie only when
            // the deletion carries Secure and Path=/, so it is built from the same options that set it.
            // R-E4: a token from before logout then pairs with no cookie in the next session.
            // https://datatracker.ietf.org/doc/html/draft-ietf-httpbis-rfc6265bis#name-the-__host-prefix
            context.Response.Cookies.Delete(options.Value.Cookie.Name!, options.Value.Cookie.Build(context));
    }
}

/// <summary>
/// Stage 9: an unsafe method on <c>/api</c> needs a valid <c>X-CSRF-TOKEN</c>, or it is refused with
/// 403 <c>csrf_failed</c> under rule <c>csrf.token</c> (<c>contracts/request-gate.md</c>).
/// </summary>
internal sealed partial class AntiforgeryStage(ICsrfTokens tokens, ILogger logger)
{
    // Case-insensitive, as the request gate's match and endpoint routing are, so /API/... cannot
    // reach the proxy route without a token.
    private static readonly PathString Api = "/api";

    /// <summary><c>contracts/request-gate.md</c> -> Rule ids.</summary>
    private const string Rule = "csrf.token";

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(Api) || IsSafe(context.Request.Method))
        {
            // /bff/logout is a form post and carries no token: the request gate's Origin check covers
            // it (ADR-0007).
            await next(context);
            return;
        }

        // A request with no session is checked too, and fails: /bff/session issues tokens only to a
        // signed-in user, so no anonymous pair exists to pass.
        if (!await tokens.IsValidAsync(context))
        {
            Refused(logger, Rule);

            // Task 4.3a: the same constant on stage 2's request line, null-conditionally because this
            // project's own tests run without stage 2 (contracts/telemetry.md -> "How `rule` and
            // `session_ref` reach the request line").
            context.Features.Get<IRequestTelemetryFeature>()?.Rule = Rule;

            await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "csrf_failed");
            return;
        }

        await next(context);
    }

    /// <summary>The safe methods in <c>contracts/http-contract.md</c> -> Request headers the SPA sends.</summary>
    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    // The rule id only. Nothing the client sent goes in the line (contracts/telemetry.md -> Never logged).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Antiforgery refused the request. rule={Rule}")]
    private static partial void Refused(ILogger logger, string rule);
}
