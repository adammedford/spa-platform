using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy;

/// <summary>
/// The answer to <c>/api/{name}</c> for a name the app does not list. The upstream contract says
/// 404 <c>unknown_api</c>, "and no upstream sees the request": this endpoint sits behind every
/// generated route, so reaching it means no route matched and nothing was forwarded.
/// </summary>
internal static class UnknownApi
{
    /// <summary>The rule id for this refusal, from <c>contracts/request-gate.md</c>.</summary>
    private const string Rule = "api.unknown";

    internal static Task WriteAsync(HttpContext context)
    {
        // The rule id goes in the log line and the body carries the code alone, as the browser
        // contract says. The path is left out of both: it is attacker-chosen.
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(UnknownApi).FullName!)
            .LogWarning("The app lists no upstream under that name. rule={Rule}", Rule);

        return Problems.WriteAsync(context, StatusCodes.Status404NotFound, "unknown_api");
    }
}
