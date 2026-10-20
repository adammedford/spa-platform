using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Proxy;

/// <summary>
/// Task 4.2: the session's access token, acquired ahead of the forwarder. A call with no token is
/// answered with a problem body, 401 or 503 by the outcome, and nothing is forwarded; a call with one
/// carries it on as an <see cref="AcquiredToken"/> for <see cref="UpstreamHeaders"/> to attach.
///
/// A step in YARP's proxy pipeline rather than a request transform: both meet the contract (G0 item 5,
/// finding T-C21), and the step makes the 401 an ordinary response, which is the plan's reason for it
/// (docs/plan/03-workstreams-and-tasks.md -> workstream 4).
/// </summary>
internal static class AccessTokenStep
{
    internal static void Use(IApplicationBuilder proxy)
    {
        proxy.Use(async (context, next) =>
        {
            // Resolved per request, so a scoped registration is honoured rather than captured. A host
            // that registers no source fails closed: with nothing to ask, no call has a token, so each
            // is answered as a call with no session is rather than forwarded bare (plan row 4.2,
            // "answer 401 when there is none").
            IAccessTokenSource? source = context.RequestServices.GetService<IAccessTokenSource>();

            // Never the request's cancellation token: a browser that disconnects mid-refresh would
            // strand the rotated refresh token (findings T-B15, T-B16). Task 3.8 holds the same rule
            // inside the source; passing None here means the proxy cannot break it from outside.
            AccessTokenResult result = source is null
                ? new AccessTokenResult.Unauthenticated()
                : await source.GetAsync(context, CancellationToken.None);

            switch (result)
            {
                case AccessTokenResult.Token token:
                    context.Features.Set(new AcquiredToken(token.Value));
                    await next(context);
                    break;

                // Answers, not rejections: each names its code alone and Problems.WriteAsync takes the
                // status from contracts/http-contract.md -> Errors (#142, Q7). Neither writes a rule id. The 401 for a live
                // session that yields no token is deliberately not a rejection (contracts/telemetry.md ->
                // "How `rule` and `session_ref` reach the request line"), and auth.session turns on
                // whether a cookie was presented (contracts/request-gate.md), which Unauthenticated does
                // not say, so the step has nothing to write it from.
                case AccessTokenResult.SessionEnded:
                    await Problems.WriteAsync(context, Problems.SessionEnded);
                    break;

                case AccessTokenResult.Unauthenticated:
                    await Problems.WriteAsync(context, Problems.Unauthenticated);
                    break;

                // The provider could not be reached and the session is kept, so not a 401, which the
                // browser reads as "log in again" (assumption A3). Not a rejection either, so no rule id
                // (change request 4.2 -> Decision).
                case AccessTokenResult.ProviderUnavailable:
                    await Problems.WriteAsync(context, Problems.IdpUnavailable);
                    break;

                // An outcome the frozen contract does not name. Never forwarded: with no token to attach
                // the call would go on bare, and that is a decision, not a default. A 500.
                default:
                    throw new InvalidOperationException(
                        $"IAccessTokenSource returned {result.GetType().Name}, which the proxy does not answer.");
            }
        });
    }
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
