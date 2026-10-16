using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Proxy;

/// <summary>
/// The answer to <c>/api/{name}</c> for a name the app does not list. The upstream contract says
/// 404 <c>unknown_api</c>, "and no upstream sees the request": this endpoint sits behind every
/// generated route, so reaching it means no route matched and nothing was forwarded.
/// </summary>
internal static class UnknownApi
{
    /// <summary>
    /// The rule id for this refusal, from <c>contracts/request-gate.md</c> -> Rule ids by way of
    /// <see cref="RuleIds"/>, which holds that table so that no owner keeps a copy (ADR-0015).
    /// </summary>
    private const string Rule = RuleIds.ApiUnknown;

    internal static Task WriteAsync(HttpContext context)
    {
        // The rule id goes in the log line and the body carries the code alone, as the browser
        // contract says. The path is left out of both: it is attacker-chosen.
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(UnknownApi).FullName!)
            .LogWarning("The app lists no upstream under that name. rule={Rule}", Rule);

        // Task 4.3a: the same constant on UsePlatformTelemetry's request line, null-conditionally
        // because this project's own tests run without UsePlatformTelemetry (contracts/telemetry.md ->
        // "How `rule` and `session_ref` reach the request line").
        context.Features.Get<IRequestTelemetryFeature>()?.Rule = Rule;

        return Problems.WriteAsync(context, StatusCodes.Status404NotFound, "unknown_api");
    }
}
