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
public sealed class ClientAdmission(IEnumerable<string> admitted)
{
    private readonly object gate = new();
    private ImmutableHashSet<string> clients = [.. admitted];

    /// <summary>The clients admitted right now.</summary>
    public ImmutableHashSet<string> Clients => clients;

    public bool IsAdmitted(string? clientId) => clientId is not null && clients.Contains(clientId);

    /// <summary>Admits a client. This is the API owner's decision, taken at run time.</summary>
    public void Admit(string clientId)
    {
        lock (gate)
        {
            clients = clients.Add(clientId);
        }
    }
}
