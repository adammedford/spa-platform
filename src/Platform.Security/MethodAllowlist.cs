using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Security;

/// <summary>
/// The methods some path of the gateway accepts, checked in stage 1 so a method no path accepts never
/// reaches a later stage. N-20: <c>TRACE</c> and <c>CONNECT</c> get 405. Kestrel passes both to the
/// application, <c>CONNECT</c> included (the HTTP/1.1 authority form), so the check is the gateway's.
///
/// Which of these a given path accepts is the request gate's rule (<c>contracts/request-gate.md</c>,
/// stage 6, task 4.3): <c>GET</c> and <c>HEAD</c> for an asset, one method for each <c>/bff</c>
/// endpoint, any of these for <c>/api</c>. Both refusals share the rule id <c>gate.method</c> and the
/// code <c>method_not_allowed</c>, as the contract's tables give them.
/// </summary>
internal static partial class MethodAllowlist
{
    private const string Rule = "gate.method";

    /// <summary>
    /// <c>/api/**</c> takes any method an API might use; everything else takes fewer. <c>OPTIONS</c>
    /// is here for the APIs' own use: the gateway sends no <c>Access-Control-*</c> header, so a
    /// browser's preflight still fails.
    /// </summary>
    private static readonly string[] Allowed =
    [
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Post, HttpMethods.Put,
        HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Options,
    ];

    private static readonly string AllowHeader = string.Join(", ", Allowed);

    internal static Task InvokeAsync(HttpContext context, RequestDelegate next, ILogger logger)
    {
        string method = context.Request.Method;
        foreach (string allowed in Allowed)
        {
            // Kestrel hands known methods over as the interned HttpMethods strings, but an unknown
            // one arrives as sent, so the comparison is the framework's own, case-sensitive by RFC 9110.
            if (string.Equals(method, allowed, StringComparison.Ordinal))
            {
                return next(context);
            }
        }

        // The rule id only: the method is the client's to choose, so it stays out of the log line.
        Refused(logger, Rule);

        // RFC 9110 section 15.5.6: a 405 carries Allow.
        context.Response.Headers.Allow = AllowHeader;
        return Problems.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No path accepts the request's method. rule={Rule}")]
    private static partial void Refused(ILogger logger, string rule);
}
