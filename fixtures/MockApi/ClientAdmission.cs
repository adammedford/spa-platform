using System.Collections.Immutable;

namespace MockApi;

/// <summary>
/// The clients one API accepts, changeable while it runs.
///
/// It is mutable for one reason, and the reason is a test: F-07 onboards app 3, the mock API refuses
/// its call because it has not admitted that client, "and in the hermetic tier the harness then
/// admits client 3 and the call succeeds". A restart between those two steps would prove the wrong
/// thing, since the whole point of F-07 is that nothing restarts.
/// </summary>
public sealed class ClientAdmission(
    IEnumerable<string> admitted, IReadOnlyDictionary<string, ImmutableArray<string>> routes)
{
    private readonly object gate = new();
    private ImmutableHashSet<string> clients = [.. admitted];
    private ImmutableDictionary<string, ImmutableHashSet<string>> routeClients =
        routes.ToImmutableDictionary(route => route.Key, route => route.Value.ToImmutableHashSet(), StringComparer.Ordinal);

    public ClientAdmission(IEnumerable<string> admitted)
        : this(admitted, ImmutableDictionary<string, ImmutableArray<string>>.Empty)
    {
    }

    /// <summary>The clients admitted right now on every route that has no admission of its own.</summary>
    public ImmutableHashSet<string> Clients => clients;

    public bool IsAdmitted(string? clientId) => clientId is not null && clients.Contains(clientId);

    /// <summary>
    /// Whether a client is admitted on one of the API's routes: by the route's own clients when it
    /// has them (<see cref="ApiListenerOptions.RouteAdmissions"/>), otherwise by <see cref="Clients"/>.
    /// </summary>
    public bool IsAdmitted(string? clientId, string route) =>
        routeClients.TryGetValue(route, out ImmutableHashSet<string>? onRoute)
            ? clientId is not null && onRoute.Contains(clientId)
            : IsAdmitted(clientId);

    /// <summary>Admits a client. This is the API owner's decision, taken at run time.</summary>
    public void Admit(string clientId)
    {
        lock (gate)
        {
            clients = clients.Add(clientId);
        }
    }

    /// <summary>Admits a client on one route, which from then on has an admission of its own.</summary>
    public void Admit(string clientId, string route)
    {
        lock (gate)
        {
            routeClients = routeClients.SetItem(
                route, (routeClients.TryGetValue(route, out ImmutableHashSet<string>? onRoute) ? onRoute : []).Add(clientId));
        }
    }
}
