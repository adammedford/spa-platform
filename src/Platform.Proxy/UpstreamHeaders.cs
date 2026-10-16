using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Platform.Proxy;

/// <summary>
/// Task 4.2's second half: the headers <c>contracts/upstream-contract.md</c> lets through, in each
/// direction, applied to every generated route.
/// </summary>
internal static class UpstreamHeaders
{
    /// <summary>
    /// Request headers the browser may not send upstream by exact name (upstream contract -> Never
    /// sent upstream). <c>Authorization</c> is here too, and the token step's goes on afterwards.
    /// </summary>
    private static readonly string[] ForbiddenRequestHeaders =
    [
        HeaderNames.Cookie,
        HeaderNames.Authorization,
        "X-CSRF-TOKEN",
        "Forwarded",
        HeaderNames.Via,
        "X-Real-IP",
    ];

    /// <summary>
    /// Request header prefixes the browser may not send upstream. A prefix and not a list, because a
    /// list of names is never complete: YARP's defaults replace only <c>X-Forwarded-For</c>,
    /// <c>-Host</c>, <c>-Proto</c> and <c>-Prefix</c>, and every other forwarding header went upstream
    /// as the browser sent it (findings T-C3, T-C5, T-C22). <c>X-Original-*</c> is where the framework's
    /// forwarded-headers middleware moves the values it consumed.
    /// </summary>
    private static readonly string[] ForbiddenRequestPrefixes = ["X-Forwarded-", "X-Original-"];

    internal static void Apply(TransformBuilderContext context)
    {
        // Strip first, then let the gateway set its own three. The order is the point: the
        // forwarders below run after this transform because they are added after it, and the
        // defaults YARP would otherwise append are switched off so nothing else, X-Forwarded-Prefix
        // included, is added behind them.
        context.AddRequestTransform(transform =>
        {
            StripAndAttach(transform);
            return ValueTask.CompletedTask;
        });

        context.UseDefaultForwarders = false;
        context.AddXForwardedFor(action: ForwardedTransformActions.Set);
        context.AddXForwardedProto(action: ForwardedTransformActions.Set);
        context.AddXForwardedHost(action: ForwardedTransformActions.Set);

        // Returned to the browser: never Set-Cookie and never Access-Control-*, "whatever the
        // upstream's status". YARP's header removal acts on successful responses only unless told
        // otherwise, and an upstream's 500 then delivered its cookie (findings T-C7, T-C20), so the
        // condition is Always.
        // https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Transforms/ResponseHeaderRemoveTransform.cs
        context.AddResponseHeaderRemove(HeaderNames.SetCookie, ResponseCondition.Always);

        // Access-Control-* is a prefix, which the built-in removal cannot express. A response transform
        // of one's own carries no condition and runs on every response YARP received, so it needs no
        // Always. UsePlatformSecurityHeaders strips the same prefix as the response starts; this one
        // keeps the proxy's half of the contract true without it.
        context.AddResponseTransform(transform =>
        {
            IHeaderDictionary headers = transform.HttpContext.Response.Headers;
            foreach (string name in headers.Keys.Where(IsAccessControl).ToList())
            {
                headers.Remove(name);
            }

            return ValueTask.CompletedTask;
        });
    }

    private static bool IsAccessControl(string name) =>
        name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase);

    private static void StripAndAttach(RequestTransformContext transform)
    {
        HttpRequestHeaders headers = transform.ProxyRequest.Headers;

        foreach (string name in ForbiddenRequestHeaders)
        {
            headers.Remove(name);
        }

        // Names read through NonValidated: enumerating the typed collection parses, and so rewrites,
        // some values (ADR-0006, G0 item 5). Collected first, because removing while enumerating
        // throws.
        List<string> prefixed = [];
        foreach (KeyValuePair<string, HeaderStringValues> header in headers.NonValidated)
        {
            if (ForbiddenRequestPrefixes.Any(prefix => header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                prefixed.Add(header.Key);
            }
        }

        foreach (string name in prefixed)
        {
            headers.Remove(name);
        }

        // The session's token, or nothing: whatever the browser sent is never the token.
        if (transform.HttpContext.Features.Get<AcquiredToken>() is { } token)
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        }
    }
}
