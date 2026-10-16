using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Platform.Abstractions;

/// <summary>
/// One issuer in platform configuration. <see cref="BrowserOrigin"/> is the origin the browser
/// visits at the provider: it goes into <c>form-action</c> and is the base of Cognito's logout URL
/// (ADR-0011). Source: docs/plan/02-contracts.md -> Interfaces. Freezes at G1a.
/// </summary>
public sealed record IssuerDefinition(string Authority, string Profile, Uri BrowserOrigin);

/// <summary>
/// What differs between identity providers, chosen per issuer and never per app (ADR-0011).
/// Frozen at G1b: G0 item 2 recorded what Cognito's discovery document advertises for logout
/// (<c>end_session_endpoint</c>, evidence C-2.2) and for revocation (<c>revocation_endpoint</c>,
/// C-2.3), and task I-6 folded that in (ADR-0017). The revocation endpoint comes from discovery,
/// so no member is needed for it.
/// </summary>
[Freeze("G1b")]
public interface IIdpProfile
{
    /// <summary>"standard" or "cognito". Selected by <c>Issuers:{name}:Profile</c>.</summary>
    string Name { get; }

    /// <summary>
    /// How this provider's client presents its secret, for the code exchange, the refresh and the
    /// revocation alike. Cognito's revocation endpoint takes only
    /// <see cref="ClientCredentialStyle.AuthorizationHeader"/> (C-2.21), so the cognito profile
    /// states that one and every call uses it.
    /// </summary>
    ClientCredentialStyle ClientCredentialStyle { get; }

    /// <summary>The provider-specific part of the OIDC options for one app.</summary>
    void Configure(OpenIdConnectOptions options, AppDefinition app, IssuerDefinition issuer);

    /// <summary>
    /// Sends the user to the provider's logout. The standard profile uses RP-initiated logout from
    /// discovery; the Cognito profile goes to the logout endpoint on the issuer's browser origin
    /// with <c>client_id</c> and <c>logout_uri</c>, landing on <c>/</c> rather than on
    /// <c>/signout-callback-oidc</c>, because Cognito returns no <c>state</c> (finding R-C7).
    /// </summary>
    Task RedirectToSignOutAsync(RedirectContext context, AppDefinition app, IssuerDefinition issuer);
}
