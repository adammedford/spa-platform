using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The stack a functional row runs against, local or deployed (#209), as the row needs it: the apps,
/// a fresh browser profile, what the browser exchanged with the cell, the provider's endpoints, the
/// gateway's log, and the levers a row pulls (token expiry, a stop at the provider's login, a
/// release switch). A clause only one stack can show (what the test provider counted, say) is
/// asserted by the row on a <see cref="LocalStack"/> alone; docs/plan/04-tests.md names those clauses
/// "In H".
/// </summary>
public interface IBrowserStack : IAsyncDisposable
{
    StackKind Kind { get; }

    HarnessApp App(string appId);

    /// <summary>A fresh browser profile: no cookies, no storage.</summary>
    Task<Profile> OpenAsync();

    /// <summary>
    /// Finishes a login the profile started with a navigation to <c>/bff/login</c>: nothing for the test
    /// provider, which approves at once; Cognito's managed login form for the cell, unless the provider
    /// still holds a session for this browser and sends it straight back.
    /// </summary>
    Task CompleteLoginAsync(Profile profile, HarnessApp app);

    /// <summary>Every request a browser on this stack made to an app host and the response it received, body included, oldest first.</summary>
    Task<IReadOnlyList<Exchange>> ExchangesAsync();

    /// <summary>The provider's host, whose own cookies are not the platform's.</summary>
    string ProviderHost { get; }

    /// <summary>The provider's authorization endpoint, as a URL with no query.</summary>
    string AuthorizeEndpoint { get; }

    /// <summary>The provider's logout endpoint, as a URL with no query.</summary>
    string EndSessionEndpoint { get; }

    /// <summary>The gateway's JSON log lines from this stack's start whose <c>msg</c> starts with <paramref name="message"/>, as they stand now.</summary>
    Task<IReadOnlyList<JsonElement>> LogLinesAsync(string message);

    /// <summary>
    /// The same, once at least one has arrived and the log has had time to carry any that came with
    /// it, so a count of them can be asserted. Fails if none comes.
    /// </summary>
    Task<IReadOnlyList<JsonElement>> SettledLogLinesAsync(string message);

    /// <summary>Signs the profile in to the app holding an access token that has expired by the time this returns (F-03).</summary>
    Task SignInWithExpiredAccessTokenAsync(Profile profile, HarnessApp app);

    /// <summary>
    /// Runs <paramref name="action"/>, which ends in a navigation to the provider's authorization
    /// endpoint, and returns once the browser is stopped at the provider's login (F-04).
    /// </summary>
    Task StopAtProviderLoginAsync(Profile profile, Func<Task> action);

    /// <summary>The release the app serves now.</summary>
    Task<string> ReleaseOfAsync(string appId);

    /// <summary>Publishes a release of the app's files, as <c>spactl release publish</c> does; serving it is <see cref="SwitchReleaseAsync"/>'s.</summary>
    Task PublishReleaseAsync(string appId, string release, IReadOnlyDictionary<string, byte[]> files);

    /// <summary>Points the app at another release in a new registry revision, with no deployment, as <c>spactl release activate</c> does.</summary>
    Task SwitchReleaseAsync(HarnessApp app, string release);

    /// <summary>How long after <see cref="SwitchReleaseAsync"/> the gateway may take to serve it: one registry poll (docs/plan/05-infrastructure-and-cost.md, Deployment and rollback).</summary>
    TimeSpan RegistryPropagation { get; }
}

/// <summary>Starts the stack <see cref="StackSwitch"/> chooses.</summary>
public static class Stacks
{
    public static async Task<IBrowserStack> StartAsync() => StackSwitch.FromEnvironment() switch
    {
        StackKind.Deployed => await DeployedStack.StartAsync(),
        _ => await LocalStack.StartAsync(),
    };
}
