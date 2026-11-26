# Identity provider contract

- **Freezes at:** On landing, after G6's tag. Prose added on a decided change request extends it.
- **Read by:** whoever runs the identity provider an app's issuer names, and whoever registers an
  app's or a fragment's OAuth client at it

What the gateway needs of an identity provider: of the issuer, once, and of each client, at each
app or fragment it serves. The gateway is the OAuth client. The browser never holds a token
(`http-contract.md`), so every client here is the gateway's, registered once per app and once per
fragment. A provider that meets this contract serves any app the registry admits under it. What
the provider does beyond it is its own.

Each issuer a registry entry names is an `Issuers:{name}` entry in the platform configuration
(`platform-config.schema.json`), read at start-up, with an `Authority`, a `Profile` and a
`BrowserOrigin`. The issuer's IdP profile, `standard` or `cognito`, chooses the two places the
gateway's requests differ: its client credential style, which is how it presents the client's
secret, and how it ends the provider's session at logout (ADR-0011, ADR-0017). Any provider but
Amazon Cognito runs as `standard`.

## The issuer

| Need | Rule |
| --- | --- |
| Discovery | An OpenID Connect discovery document under the issuer's `Authority`, reachable from the gateway, naming the authorization, token and JWKS endpoints. |
| Signing keys | A JWKS the gateway and every upstream can fetch, holding the keys that sign the ID tokens and the access tokens. |
| Access tokens | A JWT that an upstream checks as `upstream-contract.md` states: signature, `iss`, `exp`, an admitted `client_id`, the route's scope, `aud` when resource binding is on, and for Cognito `token_use` `access`. |
| ID tokens | Carry `sub`. `name` and `email` may be absent (`http-contract.md`). |
| Revocation | A revocation endpoint in discovery. The gateway copes without one (below). |
| Ending the session, under `standard` | An `end_session_endpoint` in discovery, which accepts `post_logout_redirect_uri` and `id_token_hint` (OpenID Connect RP-Initiated Logout 1.0). Under `cognito`, the profile's own logout endpoint, which takes `client_id` and `logout_uri`. |

## Each client

One client per app, and one per fragment of a shell. Every row holds for an app's client. A
fragment's client differs where "A fragment's client" says so.

| Need | Rule |
| --- | --- |
| Kind | Confidential: it holds a client secret, and only the gateway presents it. |
| Grants | The authorization code grant, with PKCE (S256) and `response_mode=query`; and the refresh grant. |
| A refresh token at login | The token response to the code exchange carries a refresh token. Without one the session ends at the first access-token expiry. The gateway asks for exactly the entry's `oauth.scopes` and adds none, so a provider that issues refresh tokens only for `offline_access` needs `offline_access` in the app definition's scopes. |
| Client authentication | The client secret, in the IdP profile's client credential style, at both the token endpoint and the revocation endpoint: in the form body under `standard`, in a Basic `Authorization` header under `cognito` (ADR-0017). |
| Callback | `https://{host}/signin-oidc`, with `{host}` exactly as the registry entry has it, port included. |
| Logout return, `standard` | `https://{host}/signout-callback-oidc`, registered as a `post_logout_redirect_uri`. |
| Logout return, `cognito` | `https://{host}/`, registered as a `logout_uri`. |
| Refresh-token validity | At least the longest session the registry allows, `session.lifetimeMinutes`'s maximum in `registry.schema.json`, so no session ends before its own expiry. |
| Access-token validity | More than one minute. The gateway refreshes a token one minute before it expires, so a shorter life refreshes on every call. |
| Refresh-token size | At most 4,096 characters, the token library's limit (ADR-0005). |
| Scopes | Every value of the entry's `oauth.scopes` is grantable to the client, `openid` among them. |
| Audience | When the entry sets `oauth.resource`, the access token's `aud` is bound to it. |
| Client id | Unique across every app and fragment the registry names. |

### A fragment's client

A fragment's client belongs to its shell's issuer. The registry refuses a fragment naming another
issuer. It registers one callback, `https://{shell host}/signin-oidc/{fragmentId}`, on the
shell's host exactly as the registry has it, and no logout return: logout ends the provider's
session once, through the shell's client. Every other row of "Each client" holds for it as for
the shell's (ADR-0038).

## What the gateway copes without

A provider may lack each of these. The gateway still serves the app, at the cost stated.

| Without | What happens |
| --- | --- |
| Refresh-token rotation, or a grace period after it | Nothing. Where the provider rotates, the gateway keeps the newest refresh token it was given and keeps the old one when an answer carries none. Refreshes are serialised per session across instances (ADR-0024), so no grace period is needed for them to succeed. A grace period only lets a refresh recover when its holder stops after the provider rotated and before the new token was stored. |
| A revocation endpoint | Logout records that there was none and still removes the session. The tokens stay valid at the provider until they expire. |
| Two active secrets on one client | A rotation of the client's secret has a gap, between the provider changing the secret and every instance reading the new one, in which refreshes and logouts for that client fail. With two active secrets, a rotation has no gap. |
| Silent authorization, `prompt=none`, for a fragment's client | A fragment's sign-in hop asks for no interaction. A provider that refuses it, or has no live session to answer it from, leaves the fragment `loginRequired` (`http-contract.md`, Fragments), and the user signs the fragment in by the control the shell offers. |

## The provider's own

The gateway constrains none of these: whether refresh expiry is absolute or idle, above the floor
in "Each client"; how long access and ID tokens live, above their floors; the provider's login
pages and branding; and every setting a provider has that this contract does not name.

## Logout under `standard`

Logout sends the session's stored ID token as `id_token_hint`. A refresh replaces the stored ID
token only when the provider's answer carries one, so on a long session the hint may have
expired. RP-Initiated Logout asks a provider to accept an expired hint. A provider that refuses
it shows its own error page. The session was already removed when the browser left the gateway,
so the user is signed out of the app either way.

## What this contract does not promise

Only what the gateway image does is promised. Client authentication by `private_key_jwt`, pushed
authorization requests, `response_mode=form_post` and back-channel logout are decided (ADR-0028)
and not built. A provider needs none of them. The change request that builds each adds its rows
here.
