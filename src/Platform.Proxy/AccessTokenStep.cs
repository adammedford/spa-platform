using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Proxy;

/// <summary>
/// Task 4.2: the session's access token, acquired ahead of the forwarder. A call with no token is
/// answered 401 with a problem body and nothing is forwarded; a call with one carries it on as an
/// <see cref="AcquiredToken"/> for <see cref="UpstreamHeaders"/> to attach.
///
/// A step in YARP's proxy pipeline rather than a request transform: both meet the contract (G0 item 5,
/// finding T-C21), and the step makes the 401 an ordinary response, which is the plan's reason for it
/// (docs/plan/03-workstreams-and-tasks.md -> workstream 4).
/// </summary>
internal static partial class AccessTokenStep
{
    internal static void Use(IApplicationBuilder proxy)
    {
        // Asked once, at map time, without resolving anything. AddPlatformIdentity registers no token
        // source until task 3.8, and the composed gateway forwards /api today (host resolution is
        // real), so a missing source is the skeleton, not a fault: the call goes on with no
        // Authorization at all, which the upstream refuses on its own (assumption A5). This is the same
        // allowance RegistryProxyConfigProvider makes for a missing registry.
        bool registered = proxy.ApplicationServices.GetService<IServiceProviderIsService>()
            ?.IsService(typeof(IAccessTokenSource)) == true;

        if (!registered)
        {
            NoTokenSource(proxy.ApplicationServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AccessTokenStep).FullName!));
            return;
        }

        proxy.Use(async (context, next) =>
        {
            // Resolved per request, so a scoped registration is honoured rather than captured.
            IAccessTokenSource source = context.RequestServices.GetRequiredService<IAccessTokenSource>();

            // Never the request's cancellation token: a browser that disconnects mid-refresh would
            // strand the rotated refresh token (findings T-B15, T-B16). Task 3.8 holds the same rule
            // inside the source; passing None here means the proxy cannot break it from outside.
            AccessTokenResult result = await source.GetAsync(context, CancellationToken.None);

            switch (result)
            {
                case AccessTokenResult.Token token:
                    context.Features.Set(new AcquiredToken(token.Value));
                    await next(context);
                    break;

                // contracts/http-contract.md -> Errors. Neither writes a rule id: the 401 for a live
                // session that yields no token is deliberately not a rejection, and a cookie that
                // resolves to no session is the shell gate's to name, as auth.session
                // (contracts/telemetry.md -> "How `rule` and `session_ref` reach the request line").
                case AccessTokenResult.SessionEnded:
                    await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "session_ended");
                    break;

                case AccessTokenResult.Unauthenticated:
                    await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthenticated");
                    break;

                // An outcome the frozen contract does not name. Never forwarded: with no token to attach
                // the call would go on bare, and that is a decision, not a default. A 500.
                default:
                    throw new InvalidOperationException(
                        $"IAccessTokenSource returned {result.GetType().Name}, which the proxy does not answer.");
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No IAccessTokenSource is registered, so API calls are forwarded with no access token.")]
    private static partial void NoTokenSource(ILogger logger);
}

/// <summary>
/// The token <see cref="AccessTokenStep"/> acquired for this request, as a request feature. Internal
/// and never logged: <c>contracts/telemetry.md</c> -> Never logged.
/// </summary>
internal sealed record AcquiredToken(string Value)
{
    /// <summary>Kept out of any string a log or an exception could carry.</summary>
    public override string ToString() => nameof(AcquiredToken);
}
