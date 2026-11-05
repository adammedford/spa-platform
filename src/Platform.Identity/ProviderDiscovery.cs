using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// A challenge whose provider's discovery document cannot be fetched (#908, #543's held row). Discovery is
/// read on the first challenge, not at start-up (<see cref="IssuerDirectory"/>), and the handler's challenge
/// reads it before it writes anything; left alone, the identity model's <c>InvalidOperationException</c>
/// (IDX20803) leaves the challenge and the server answers <c>500</c> with no body.
///
/// Read here first instead, through the options' own manager, so the handler's read that follows is the
/// cached document. When it cannot be read, a navigation is answered <c>503</c> with the static failure
/// page, whose link tries again, and anything else with the <c>401</c> problem a fetch always gets, since a
/// fetch's answer never depended on the provider (ADR-0008). Neither carries a rule id: nothing the
/// request sent was refused. The identity model keeps no failed read, so the next challenge asks the
/// provider again and logins resume when it answers.
///
/// One Warning per request names the issuer and the type of the exception that caused the failure, under
/// this type's category, which the telemetry floor keeps (the framework handler's is floored to None). The
/// exception is not attached and its message not written: the message holds the discovery URL (N-16).
/// </summary>
internal static partial class ProviderDiscovery
{
    /// <summary>
    /// Reads the discovery document the challenge needs. True when the challenge may go on; false when this
    /// request has been answered for an unreadable document, or ended by the browser and left unanswered.
    /// </summary>
    public static async Task<bool> ReadAsync(HttpContext context, OpenIdConnectOptions options)
    {
        if (options.Configuration is not null || options.ConfigurationManager is not { } manager)
        {
            return true;
        }

        try
        {
            OpenIdConnectConfiguration _ = await manager.GetConfigurationAsync(context.RequestAborted);
            return true;
        }
        catch (InvalidOperationException e)
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                // The read ended because the browser went: no outage to report and nobody to answer.
                return false;
            }

            Log(context, e);
            await AnswerAsync(context);
            return false;
        }
    }

    private static Task AnswerAsync(HttpContext context)
    {
        if (!AuthenticationEvents.IsNavigation(context.Request))
        {
            return Problems.WriteAsync(context, Problems.Unauthenticated);
        }

        HttpResponse response = context.Response;
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        return response.WriteAsync(AuthenticationEvents.FailurePage, context.RequestAborted);
    }

    private static void Log(HttpContext context, InvalidOperationException failure)
    {
        // The identity model wraps the cause twice: its manager's IDX20803 around its retriever's IOException,
        // which wraps what the HTTP client threw (refused, timed out) or, for an answer that was not 200,
        // stands alone. The type logged is the innermost of those two levels that exists.
        Exception cause = failure.InnerException?.InnerException ?? failure.InnerException ?? failure;
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProviderDiscovery).FullName!);
        Unreadable(logger, context.Features.Get<IAppFeature>()?.App.OAuth.Issuer ?? "(none)", cause.GetType().Name);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The discovery document of issuer {Issuer} could not be read ({ExceptionType}). The challenge is answered "
            + "503 for a navigation and 401 for anything else, and no login starts.")]
    private static partial void Unreadable(ILogger logger, string issuer, string exceptionType);
}
