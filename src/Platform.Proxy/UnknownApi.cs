using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Proxy;

/// <summary>
/// The answer to <c>/api/{name}</c> for a name the app does not list. The upstream contract says
/// 404 <c>unknown_api</c>, "and no upstream sees the request": this endpoint sits behind every
/// generated route, so reaching it means no route matched and nothing was forwarded.
///
/// It logs its own Warning line, then ends under <c>api.unknown</c> through
/// <see cref="Rejection.EndAsync"/>, which notes the rule for the request line and writes the problem
/// body the contracts' two tables give that id (#142, Q9; #162).
/// </summary>
internal static partial class UnknownApi
{
    /// <summary>
    /// The rule id for this rejection, from <c>contracts/request-gate.md</c> -> Rule ids by way of
    /// <see cref="RuleIds"/>, which holds that table so that no owner keeps a copy (ADR-0015).
    /// </summary>
    private const string Rule = RuleIds.ApiUnknown;

    internal static Task WriteAsync(HttpContext context)
    {
        // The rule id goes in the log line and the body carries the code alone, as the browser
        // contract says. The path is left out of both: it is attacker-chosen. The category is the
        // type's full name, as it was before the template.
        LogRejected(
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UnknownApi).FullName!),
            Rule);

        return Rejection.EndAsync(context, Rule);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The app lists no upstream under that name. rule={Rule}")]
    private static partial void LogRejected(ILogger logger, string rule);
}
