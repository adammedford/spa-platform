using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Abstractions;

namespace Platform.Proxy;

/// <summary>
/// The route constraint that lets the fragment catch-all match only the fragment host resolution
/// pinned as <see cref="IFragmentFeature"/>. Endpoint routing evaluates a constraint with the request,
/// so a path whose segment names no pinned fragment is left unmatched rather than answered
/// <c>unknown_api</c>: an unknown <c>fragmentId</c> is not routed (#996).
/// https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.routing.irouteconstraint.match
/// </summary>
internal sealed class PinnedFragment : IRouteConstraint
{
    /// <summary>The route parameter naming the fragment's segment.</summary>
    internal const string Parameter = "fragmentId";

    public bool Match(
        HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Routing matches a segment without regard to case and the pin compares as host resolution
        // did, so the segment counts when it names the pinned fragment in any case; the pin itself is
        // what says the app lists it.
        return httpContext?.Features.Get<IFragmentFeature>() is { } pinned
            && values.TryGetValue(routeKey, out object? segment)
            && string.Equals(segment as string, pinned.Fragment.FragmentId, StringComparison.OrdinalIgnoreCase);
    }
}
