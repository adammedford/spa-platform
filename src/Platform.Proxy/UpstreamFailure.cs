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

        await Problems.WriteAsync(
            context,
            context.Response.StatusCode,
            context.Response.StatusCode == StatusCodes.Status504GatewayTimeout
                ? "upstream_timeout"
                : "upstream_unavailable");
    });
}
