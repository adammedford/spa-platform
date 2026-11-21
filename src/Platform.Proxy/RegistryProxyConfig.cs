using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Platform.Abstractions;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Platform.Proxy;

/// <summary>
/// The routes and clusters one registry snapshot produces: task 4.1, and ADR-0006's decision in
/// code. One route per app and API, matched on the app's host and <c>/api/{name}/**</c>, with the
/// prefix removed, to the one upstream the entry names; and one per fragment and fragment API, on
/// <c>/fragments/{fragmentId}/api/{name}/**</c> the same way (#996).
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
    internal static RegistryProxyConfig NoRoutes() => new([], []);

    /// <summary>The configuration for a snapshot, built once and never mutated.</summary>
    public static RegistryProxyConfig From(RegistrySnapshot snapshot)
    {
        List<RouteConfig> routes = [];
        List<ClusterConfig> clusters = [];

        foreach (AppDefinition app in snapshot.ByAppId.Values)
        {
            foreach (ApiDefinition api in app.Apis)
            {
                Add(routes, clusters, app, fragmentId: null, api, new PathString(ShellApi));
            }

            // A fragment's names are its own namespace, so the same name and the same upstream under
            // the shell and a fragment are two routes, each with its own id and, through the token
            // step's cross-check, its own client's token (contracts/upstream-contract.md -> Fragment
            // routes).
            foreach (FragmentDefinition fragment in app.Fragments)
            {
                foreach (ApiDefinition api in fragment.Apis)
                {
                    Add(routes, clusters, app, fragment.FragmentId, api, PlatformPaths.FragmentApi(fragment.FragmentId));
                }
            }
        }

        return new RegistryProxyConfig(routes, clusters);
    }

    /// <summary>
    /// Signals that this configuration is no longer live, after its replacement is in place. YARP
    /// reloads on the token, so the order matters: replace, then revoke.
    /// </summary>
    internal void Revoke() => revoked.Cancel();

    /// <summary>
    /// The route metadata key naming the app a route belongs to, which the token step compares with
    /// <see cref="IAppFeature"/>.
    /// </summary>
    internal const string AppIdKey = "platform.appId";

    /// <summary>
    /// The route metadata key naming the fragment a fragment's route belongs to, absent on a shell's
    /// route, which the token step compares with <see cref="IFragmentFeature"/>
    /// (contracts/pipeline.md -> Which token set a request uses).
    /// </summary>
    internal const string FragmentIdKey = "platform.fragmentId";

    /// <summary>The prefix the browser contract puts a shell's API behind.</summary>
    private const string ShellApi = "/api";

    private static void Add(
        List<RouteConfig> routes,
        List<ClusterConfig> clusters,
        AppDefinition app,
        string? fragmentId,
        ApiDefinition api,
        PathString apiPrefix)
    {
        string id = RouteId(app.AppId, fragmentId, api.Name);
        string prefix = apiPrefix.Add("/" + api.Name).Value!;

        Dictionary<string, string> metadata = new(StringComparer.Ordinal) { [AppIdKey] = app.AppId };
        if (fragmentId is not null)
        {
            metadata[FragmentIdKey] = fragmentId;
        }

        routes.Add(new RouteConfig
        {
            RouteId = id,
            ClusterId = id,
            Match = new RouteMatch { Hosts = [app.Host], Path = prefix + "/{**rest}" },
            Metadata = metadata,
        }.WithTransformPathRemovePrefix(prefix));
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

            // The HTTP/1.1 connection bound for this one API, a constant so that YARP keeps the
            // cluster's handler, and its pool, across a registry swap (ProxyLimits; #738).
            HttpClient = new HttpClientConfig { MaxConnectionsPerServer = ProxyLimits.MaxHttp11ConnectionsPerApi },
        });
    }

    /// <summary>
    /// <c>{appId}/{name}</c> for a shell's API, <c>{appId}/fragments/{fragmentId}/{name}</c> for a
    /// fragment's. Unique, because an app id, a fragment id within its app and a name within its
    /// namespace each are, and a shell's id has no <c>fragments/</c> segment where an API name could
    /// collide with one (an API name holds no <c>/</c>).
    /// </summary>
    private static string RouteId(string appId, string? fragmentId, string apiName) =>
        fragmentId is null ? appId + "/" + apiName : appId + "/fragments/" + fragmentId + "/" + apiName;
}
