using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Platform.Proxy;

/// <summary>
/// The problem body for a forward that did not come back, from the upstream contract's two rows:
/// 502 <c>upstream_unavailable</c> when the upstream could not be reached, and 504
/// <c>upstream_timeout</c> when it exceeded the entry's timeout.
///
/// YARP sets the status and leaves the body empty, so this fills the body in. Nothing has been sent
/// when the failure happened before the upstream's first body byte; once the response has started,
/// the status is already on the wire and there is nothing to say (finding T-C18: headers followed by
/// silence give 502, and that one has started).
///
/// Only those two statuses get a body. YARP answers some failures with a status of its own, 400 when
/// the client gave up mid-body among them, and the browser contract gives 400 a different code
/// (<c>invalid_request</c>) that belongs to the request gate. Writing <c>upstream_unavailable</c>
/// over any status that is not 502 would say the upstream failed when the client did.
/// </summary>
internal static class UpstreamFailure
{
    internal static void Use(IApplicationBuilder proxy) => proxy.Use(async (context, next) =>
    {
        // Task 4.2 puts the token step ahead of this one: it short-circuits with 401 before anything
        // is forwarded, so it belongs outside the failure check rather than inside it.
        await next(context);

        if (context.Response.HasStarted || context.GetForwarderErrorFeature() is null)
        {
            return;
        }

        string? code = context.Response.StatusCode switch
        {
            StatusCodes.Status504GatewayTimeout => "upstream_timeout",
            StatusCodes.Status502BadGateway => "upstream_unavailable",
            _ => null,
        };

        if (code is not null)
        {
            await Problems.WriteAsync(context, context.Response.StatusCode, code);
        }
    });
}
