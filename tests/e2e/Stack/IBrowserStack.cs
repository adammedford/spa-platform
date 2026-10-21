using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The stack a functional row (#209) or a negative row (#211) runs against, local or deployed, as the
/// row needs it: the apps, a fresh browser profile, what the browser exchanged with the cell, the
/// provider's endpoints, the gateway's log and the rule ids in it, a raw client, a foreign page, and
/// the levers a row pulls (token expiry, a stop at the provider's login or at the callback, a release
/// switch). A clause only one stack can show (what the test provider counted, say) is
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

    /// <summary>The parent domain every app host sits under, without the leading dot.</summary>
    string ParentDomain { get; }

    /// <summary>
    /// A hostile sibling under the app domain, on the app hosts' port: a host no app has, whose page
    /// <see cref="OpenForeignPageAsync"/> serves (N-07, N-24, N-25, N-26).
    /// </summary>
    Uri Sibling => new($"https://{ForeignSites.SiblingLabel}.{ParentDomain}:{App("app1").Origin.Port}/");

    /// <summary>
    /// The user the provider signs the next login in as: one of the test users of
    /// fixtures/cognito/pool.json, so a row reads the same on both stacks. A browser the provider
    /// already knows is signed in as whoever it was signed in as then.
    /// </summary>
    void SignInAs(string user);

    /// <summary>Where the gateway's log stands now, for <see cref="AssertRuleLoggedAsync"/>.</summary>
    LogMark MarkLog();

    /// <summary>
    /// Waits for a request line written after <paramref name="after"/> that carries
    /// <paramref name="rule"/> (for <paramref name="appId"/>, when given), and fails if none comes. The
    /// rule id is in the log line and nowhere a browser can see (contracts/request-gate.md -> "Rule
    /// ids"), and the line is written after the response, so the browser may be ahead of it
    /// (contracts/telemetry.md).
    /// </summary>
    Task AssertRuleLoggedAsync(LogMark after, string rule, string? appId = null);

    /// <summary>
    /// A request no browser would send, to the stack's load balancer: the D tier's raw HTTP client
    /// (docs/plan/04-tests.md -> "How the suites run", <see cref="RawClient"/>).
    /// </summary>
    Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request);

    /// <summary>
    /// Runs <paramref name="start"/>, which begins a login to the app, completes the provider's part,
    /// and stops the browser at the app's callback: the request to <c>/signin-oidc</c> with the code and
    /// state never reaches the gateway, the browser keeps the login-state cookies, and the code is
    /// unredeemed. Returns the callback URL.
    /// </summary>
    Task<Uri> StopAtCallbackAsync(Profile profile, HarnessApp app, Func<Task> start);

    /// <summary>
    /// Opens, in the profile's page, a page some other party serves at <paramref name="url"/>:
    /// <see cref="Sibling"/>, or <see cref="ForeignSites.Elsewhere"/>. Every request the page makes
    /// leaves the browser as usual.
    /// </summary>
    Task OpenForeignPageAsync(Profile profile, Uri url, string html);

    /// <summary>How long after <see cref="SwitchReleaseAsync"/> the gateway may take to serve it: one registry poll (docs/plan/05-infrastructure-and-cost.md, Deployment and rollback).</summary>
    TimeSpan RegistryPropagation { get; }
}

/// <summary>
/// Where a stack's gateway log stood at one moment: the count of request lines on the local stack,
/// whose captured log is exact, and the time on the cell, whose CloudWatch reads are by time.
/// </summary>
public readonly record struct LogMark(int Lines, DateTimeOffset At);

/// <summary>Starts the stack <see cref="StackSwitch"/> chooses.</summary>
public static class Stacks
{
    public static async Task<IBrowserStack> StartAsync() => StackSwitch.FromEnvironment() switch
    {
        StackKind.Deployed => await DeployedStack.StartAsync(),
        _ => await LocalStack.StartAsync(),
    };
}
