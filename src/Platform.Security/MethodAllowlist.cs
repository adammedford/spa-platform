using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// The methods some path of the gateway accepts, checked in <c>UsePlatformForwardedHeaders</c> so a
/// method no path accepts never reaches a later stage. N-20: <c>TRACE</c> and <c>CONNECT</c> get 405.
/// Kestrel passes both to the application, <c>CONNECT</c> included (the HTTP/1.1 authority form), so
/// the check is the gateway's.
///
/// Which of these a given path accepts is the request gate's rule (<c>contracts/request-gate.md</c>,
/// <c>UsePlatformRequestGate</c>, task 4.3): <c>GET</c> and <c>HEAD</c> for an asset, one method for
/// each <c>/bff</c> endpoint, any of these for <c>/api</c>. Both refusals share the rule id
/// <c>gate.method</c> and the code <c>method_not_allowed</c>, as the contract's tables give them.
/// </summary>
internal static partial class MethodAllowlist
{
    /// <summary>
    /// <see cref="RuleIds"/> holds <c>contracts/request-gate.md</c>'s table, so this refusal and the
    /// request gate's share one spelling (ADR-0015). <c>UsePlatformTelemetry</c> runs before
    /// <c>UsePlatformForwardedHeaders</c> (ADR-0018), so this id reaches the request line and
    /// <c>security.rejections</c> as well as the Warning line, like every other rejection's
    /// (<c>contracts/telemetry.md</c>).
    /// </summary>
    private const string Rule = RuleIds.GateMethod;

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
        LogRejected(logger, Rule);

        // The same constant on the request line UsePlatformTelemetry writes, and the field it counts
        // security.rejections by (contracts/telemetry.md -> "How `rule` and `session_ref` reach the
        // request line"), then the 405 with Allow, which RFC 9110 section 15.5.6 requires: all seven
        // methods, since no path takes this one (#142, Q13).
        return Rejection.EndMethodAsync(context, Allowed);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No path accepts the request's method. rule={Rule}")]
    private static partial void LogRejected(ILogger logger, string rule);
}
