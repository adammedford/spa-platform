using System.Collections.Immutable;

namespace MockApi;

/// <summary>
/// How the mock API is configured. Task 1.4 supplies it through the task environment, with each
/// listener's admitted clients set from the client ids the apps root created.
/// </summary>
/// <param name="Issuer">
/// The <c>iss</c> every token must carry. Checked against this value and not against whatever the
/// metadata document happens to say, so a token from another issuer is refused even when that issuer
/// is reachable.
/// </param>
/// <param name="MetadataAddress">
/// Where the signing keys come from: the issuer's discovery document. Cognito in the deployed tier,
/// the test identity provider in the hermetic one.
/// </param>
public sealed record MockApiOptions(
    string Issuer,
    string MetadataAddress,
    ImmutableArray<ApiListenerOptions> Listeners)
{
    /// <summary>
    /// The two listeners the plan fixes, on the ports the app definitions in <c>fixtures/apps/</c>
    /// name as their upstreams. The clients are filled in from the infrastructure outputs, because a
    /// client id is created by Terraform and known to nobody before that.
    /// </summary>
    public static MockApiOptions Default(string issuer, string metadataAddress, string app1Client, string app2Client) =>
        new(
            issuer,
            metadataAddress,
            [
                new ApiListenerOptions(
                    Name: "orders",
                    Port: 5080,
                    ScopeIdentifier: "https://orders.spa-poc.test",
                    // Port 5080 checks aud, because app 1 uses resource binding.
                    Audience: "https://orders.spa-poc.test",
                    AdmittedClients: [app1Client]),
                new ApiListenerOptions(
                    Name: "reports",
                    Port: 5081,
                    ScopeIdentifier: "https://reports.spa-poc.test",
                    // Port 5081 does not, because app 2 has no resource binding.
                    Audience: null,
                    AdmittedClients: [app2Client]),
            ]);
}

/// <summary>
/// One listener, standing for one API.
/// </summary>
/// <param name="Port">The loopback port the gateway's registry entry names. 0 lets the operating system pick.</param>
/// <param name="ScopeIdentifier">
/// The resource server's identifier, which the scopes are named from: <c>{identifier}/read</c> and
/// <c>{identifier}/admin</c>. Separate from <paramref name="Audience"/> on purpose, because an API
/// with no resource binding still has scopes.
/// </param>
/// <param name="Audience">
/// The audience the token must be bound to, or null for an API that does not check one. Null is not
/// laxity: app 2 passes no <c>resource</c>, so its token carries no <c>aud</c> at all, and an API
/// that demanded one would refuse every call its own app makes.
/// </param>
/// <param name="AdmittedClients">
/// The clients this API accepts. Admission is the API owner's decision and not the gateway's, which
/// is what F-07 shows: app 3 logs in and is still refused here.
/// </param>
public sealed record ApiListenerOptions(
    string Name,
    int Port,
    string ScopeIdentifier,
    string? Audience,
    ImmutableArray<string> AdmittedClients)
{
    /// <summary>The scope <c>/whoami</c> and <c>/echo</c> need.</summary>
    public string ReadScope => ScopeIdentifier + "/read";

    /// <summary>
    /// The scope <c>/admin</c> needs. No test app asks for it, which is why F-08 expects 403 there:
    /// the route exists and the scope exists, so the refusal is about scope and not about a typo.
    /// </summary>
    public string AdminScope => ScopeIdentifier + "/admin";
}
