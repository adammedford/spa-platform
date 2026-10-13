using Microsoft.Extensions.Primitives;
using Platform.Abstractions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Platform.Proxy;

/// <summary>
/// The routes and clusters one registry snapshot produces: task 4.1, and ADR-0006's decision in
/// code. One route per app and API, matched on the app's host and <c>/api/{name}/**</c>, with the
/// prefix removed, to the one upstream the entry names.
///
/// Nothing here reads the request. A destination comes from the snapshot or from nowhere, which is
/// what makes threat T5, a steerable proxy, unreachable.
/// </summary>
public sealed class RegistryProxyConfig : IProxyConfig
{
    /// <summary>A cluster has one destination, so the name is a formality.</summary>
    private const string OnlyDestination = "only";

    private readonly CancellationTokenSource revoked = new();

    private RegistryProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        Routes = routes;
        Clusters = clusters;
        ChangeToken = new CancellationChangeToken(revoked.Token);
    }

    public IReadOnlyList<RouteConfig> Routes { get; }

    public IReadOnlyList<ClusterConfig> Clusters { get; }

    public IChangeToken ChangeToken { get; }

    /// <summary>No apps, and so no routes. The skeleton, before task 2.2 registers a registry.</summary>
    internal static RegistryProxyConfig Empty => new([], []);

    /// <summary>The configuration for a snapshot, built once and never mutated.</summary>
    public static RegistryProxyConfig From(RegistrySnapshot snapshot)
    {
        List<RouteConfig> routes = [];
        List<ClusterConfig> clusters = [];

        foreach (AppDefinition app in snapshot.ByAppId.Values)
        {
            foreach (ApiDefinition api in app.Apis)
            {
                string id = RouteId(app.AppId, api.Name);

                routes.Add(new RouteConfig
                {
                    RouteId = id,
                    ClusterId = id,
                    Match = new RouteMatch { Hosts = [app.Host], Path = PathFor(api.Name) },
                }.WithTransformPathRemovePrefix(PrefixFor(api.Name)));
                clusters.Add(new ClusterConfig
                {
                    ClusterId = id,
                    Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                    {
                        [OnlyDestination] = new DestinationConfig { Address = api.Upstream.AbsoluteUri },
                    },

                    // An idle limit: YARP restarts it on the response headers and on every body read
                    // or write, so it bounds silence rather than the whole exchange (T-C17, T-C18).
                    HttpRequest = new ForwarderRequestConfig { ActivityTimeout = api.Timeout },
                });
            }
        }

        return new RegistryProxyConfig(routes, clusters);
    }

    /// <summary>
    /// Signals that this configuration is no longer live, after its replacement is in place. YARP
    /// reloads on the token, so the order matters: replace, then revoke.
    /// </summary>
    internal void Revoke() => revoked.Cancel();

    /// <summary>An app and one of its API names. Unique, because both parts are.</summary>
    public static string RouteId(string appId, string apiName) => appId + "/" + apiName;

    /// <summary>The prefix the browser contract puts an API behind, and the proxy removes.</summary>
    private static string PrefixFor(string apiName) => "/api/" + apiName;

    /// <summary>Everything under the prefix, forwarded as it came.</summary>
    private static string PathFor(string apiName) => PrefixFor(apiName) + "/{**rest}";
}
