using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// <c>UsePlatformSecurityHeaders</c>: the app's content security policy and the fixed header set,
/// on every response, overwriting any copy an upstream sent. Task 4.5; F-06's per-app headers and N-24.
/// The header set and the baseline policy are <c>contracts/http-contract.md</c> -> Response headers;
/// a shell's policy merges every fragment's additions (#996).
/// </summary>
internal sealed partial class SecurityHeaders(IConfiguration configuration, ILogger logger)
{
    /// <summary>
    /// One policy per entry. An <see cref="AppDefinition"/> is replaced whole when the registry swaps,
    /// so a changed entry is a new key, and the old one's policy goes with the old record.
    /// </summary>
    private readonly ConditionalWeakTable<AppDefinition, string> policies = [];

    internal Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Host resolution pins the app before this stage runs. Without it there is no policy to send,
        // and sending none would be failing open, so a misordered pipeline throws, as the request gate
        // and antiforgery do.
        AppDefinition app = context.Features.Get<IAppFeature>()?.App
            ?? throw new InvalidOperationException(
                "UsePlatformSecurityHeaders runs after UsePlatformHostResolution, which sets IAppFeature.");

        string policy = policies.GetValue(app, Build);

        // OnStarting rather than a write here: the headers are applied as the response begins, after
        // every later stage has written its own. That is what makes the contract's "overwrite what the
        // upstream sent" true, because YARP copies the upstream's headers into the response before the
        // body starts, and a refusal written by the request gate or antiforgery gets the same set.
        // https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpresponse.onstarting
        context.Response.OnStarting(() =>
        {
            Apply(context.Response.Headers, policy);
            return Task.CompletedTask;
        });

        return next(context);
    }

    private static void Apply(IHeaderDictionary headers, string policy)
    {
        // The indexer replaces every value under the name, so an upstream's copy cannot survive beside
        // the platform's.
        headers.ContentSecurityPolicy = policy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers.StrictTransportSecurity = "max-age=31536000";

        // No Access-Control-* header, ever (contracts/http-contract.md -> Response headers): the gateway
        // grants no cross-origin reads, and an upstream that tries is overruled. Collected first, because
        // removing while enumerating the dictionary throws.
        foreach (string name in headers.Keys.Where(IsAccessControl).ToList())
        {
            headers.Remove(name);
        }
    }

    // The app, the fragment and the directive, not the source: the entry is the registry's to read,
    // and the line says where to look.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A content security policy source was dropped: it carries a separator or whitespace. app={AppId} directive={Directive}")]
    private static partial void SourceDropped(ILogger logger, string appId, string directive);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A content security policy source was dropped: it carries a separator or whitespace. app={AppId} fragment={FragmentId} directive={Directive}")]
    private static partial void FragmentSourceDropped(ILogger logger, string appId, string fragmentId, string directive);

    private static bool IsAccessControl(string name) =>
        name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The host's one policy: <see cref="ContentSecurityPolicy.Merge"/>, the pure function spactl's
    /// import-map check also reads, so the policy sent and the policy checked cannot differ (#961 -> "The
    /// write check"; #996). It is the baseline, then the shell's additions, then each fragment's in
    /// registry order, de-duplicated, with the issuer's browser origin
    /// (<c>Issuers:{name}:BrowserOrigin</c>, read as written: a <see cref="Uri"/> would add a trailing
    /// slash) in <c>form-action</c>. A source the function left out is logged here by where it came from.
    /// </summary>
    private string Build(AppDefinition app)
    {
        string origin = configuration[$"Issuers:{app.OAuth.Issuer}:BrowserOrigin"]
            ?? throw new InvalidOperationException(
                $"Issuer '{app.OAuth.Issuer}' has no Issuers:{{name}}:BrowserOrigin, which form-action needs.");

        MergedContentSecurityPolicy merged = ContentSecurityPolicy.Merge(app, origin);
        foreach ((string? fragmentId, string directive) in merged.Dropped)
        {
            if (fragmentId is null)
            {
                SourceDropped(logger, app.AppId, directive);
            }
            else
            {
                FragmentSourceDropped(logger, app.AppId, fragmentId, directive);
            }
        }

        return merged.HeaderValue();
    }
}
