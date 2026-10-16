namespace Platform.Abstractions;

/// <summary>
/// How a confidential client presents its secret at the provider's token and revocation
/// endpoints. Chosen per issuer by <see cref="IIdpProfile"/>, because it is a property of the
/// provider rather than of a deployment of it (ADR-0011, ADR-0017).
///
/// <b>The SPA is not the client; the gateway is.</b> A single-page app that ran the flow itself
/// would be a public client, with PKCE and no secret, and the tokens would sit in the browser. This
/// platform is a backend-for-frontend instead: the browser holds a session cookie and nothing else,
/// so each app's client is confidential, its secret lives in Parameter Store (ADR-0003), and only
/// the gateway ever presents it. This type governs those three server-to-server calls — the code
/// exchange, the refresh and the revocation — and nothing the browser does.
///
/// PKCE is used as well, not instead. Task 3.2 configures it on every login, and ADR-0007 leans on
/// it when it accepts that a code appears in a URL and in load-balancer logs. PKCE binds the code to
/// the login attempt that started it; the secret authenticates the client. A confidential client
/// wants both.
///
/// Gate G0 found that Cognito accepts either style for the code exchange and the refresh, but
/// refuses a form-body secret at the revocation endpoint with <c>401 Unauthorized</c> and accepts
/// a Basic header there (evidence C-2.19, C-2.21). One style therefore serves all three calls: per call
/// site it would be three places to get wrong, and the one that fails is only exercised at logout.
///
/// AWS requires Basic for a secret client at revocation:
/// https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html
///
/// The names match the token-management library's own vocabulary so the mapping in
/// <c>Platform.Identity</c> is obvious, but this is the platform's own type: A-04 keeps that
/// package out of every project but <c>Platform.Identity</c>.
///
/// Source: docs/plan/02-contracts.md -> Interfaces. Frozen at G1b.
/// </summary>
public enum ClientCredentialStyle
{
    /// <summary>
    /// <c>client_id</c> and <c>client_secret</c> in the form body. The default for a provider that
    /// accepts it everywhere, and what the OpenID Connect handler sends on the code exchange.
    /// </summary>
    PostBody = 0,

    /// <summary>
    /// HTTP Basic authentication, the secret in the <c>Authorization</c> header. What Cognito's
    /// revocation endpoint requires (C-2.19, C-2.21).
    /// </summary>
    AuthorizationHeader = 1,
}
