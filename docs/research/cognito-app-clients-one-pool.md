# Cognito app clients in one user pool

- **Ticket:** #767, "Cognito app clients in one user pool: silent re-auth across clients, redirect URIs,
  scopes and audience claims, revocation, logout and quotas" (`wayfinder:research`), on the map #765.
- **Date:** 2026-11-03. **Branch:** `research/cognito-app-clients-one-pool`, pushed and never merged, as
  the map rules.
- **Method:** docs-only, as the ticket and the map require. Primary sources: the Amazon Cognito developer
  guide and the user pools API reference (read through the AWS documentation tools, which fetch pages
  from docs.aws.amazon.com and touch no account), the Terraform AWS provider's resource documentation at
  the tag the apps root locks (`v6.68.0`, fetched from the provider's source repository), and this
  repository at `origin/main` `e7ffb47`: `infra/`, the two schemas, ADR-0017 with the G0 spike's saved
  output, the two runbooks, the mock API and `Platform.Identity`. Every source was read on 2026-11-03.
  **Nothing touched AWS**: no `aws` or Terraform command ran, no credential was read, no cell was
  described or changed (`docs/agents/aws-rules.md`), and no container engine exists on this machine.
  Every claim about provider behaviour is therefore read, not executed, with one exception: where a
  G0 observation from 2026-10-16 (`spikes/g0/cognito/output-cognito.txt`) already recorded the
  behaviour, it is cited as observed then, not re-run now. Section 8 lists what ran and what was read,
  and section 9 what only a live check can settle.

## Answer in brief

1. **Silent re-auth works, for one hour from the last interactive sign-in, and nothing the gateway does
   extends it.** Managed login sets a browser cookie on the pool's domain at sign-in; with it a user
   "can sign in again with the same authentication method for one hour"; "authentication with the
   session cookie doesn't reset the cookie duration"; and a token refresh "doesn't renew the managed
   login session cookie" ([managed login][ml], [refresh tokens][refresh]). So a fragment's lazy login
   is silent only while the shell's interactive login is less than an hour old. After that it prompts
   for a password (and MFA, if the pool asks for it), although the shell's gateway session, 480
   minutes in the fixtures, is still live. Only an interactive sign-in by any client in the pool starts
   a new hour. MFA does not break the cookie: "changes to user attributes or authentication factors
   have no effect on their ability to sign in again with their browser cookie" ([managed login][ml]).
2. **Silent means no page, on managed login.** The G0 run observed app2's login, after app1's, go
   `/oauth2/authorize` straight to `/signin-oidc` with no `/login` hop and no password (C-2.6, observed
   2026-10-16 on managed login version 2, two hosts in one pool, not re-run). The docs state the default:
   with no `prompt`, "users must sign in unless their browser has a valid managed login session cookie"
   ([authorize][authz]). `prompt` exists "in the managed login branding version only, not in the classic
   hosted UI": `prompt=none` "silently continues authentication for users who have a valid authenticated
   session", "between different app clients in your user pool", and otherwise returns
   `error=login_required` to the redirect URI; `prompt=login` forces re-authentication and "assigns them a
   new session cookie" ([authorize][authz]). `max_age` is documented only under step-up authentication,
   a feature "rolling out gradually": past it the user "must authenticate again from scratch"
   ([authorize][authz]); whether it acts alone, without `acr_values`, is unverified (section 9). The
   classic hosted UI is different: AWS's knowledge centre describes a "Sign in as *username*" button that
   the user must choose, which resets the hour ([re:Post logout][repost]). The cell's domain is on
   managed login version 2 (`infra/modules/idp-cognito/main.tf`), so the classic behaviour does not apply
   here.
3. **Branding styles cap the pool at 20 clients, and the quota is not adjustable.** "Managed login
   branding styles per user pool: 20, adjustable: No" ([quotas][quotas]). A style "is linked to an app
   client until you delete it" (`ClientId` required, [CreateManagedLoginBranding][cmlb]), so the shell's
   style cannot be shared with its fragments, and a client created by API "has no branding style"; "to
   use managed login, create a branding style" ([`ManagedLoginVersion`][mlv]). The apps root creates one
   style per client for that reason. The pool is the cell's, so 20 bounds shell plus fragments plus every
   other app in the cell, not fragments per shell. Whether a style-less client could still complete a
   cookie-only, page-less authorization is a live check (section 9), but a fragment's first login in a
   fresh browser, or after the hour, needs pages anyway, so treat 20 as binding. App clients themselves
   are 1,000 per pool, adjustable to 10,000 ([quotas][quotas]).
4. **Both redirect-URI shapes are registrable; Cognito constrains nothing here.** A callback URL must be
   absolute, pre-registered on the client, without a fragment component and HTTPS (except localhost),
   custom TCP ports allowed; up to 100 per client, each up to 1,024 characters
   ([authorize][authz], [CreateUserPoolClient][cupc]). No text read forbids N clients registering the
   same URL, and nothing requires a URL to be unique in the pool. Matching is OAuth 2.0's redirection
   endpoint rule, which the Cognito page cites rather than restates: a registered full URI is compared
   as a whole ([RFC 6749 §3.1.2.3][rfc6749]); the spike registered exactly one URL per client and sent
   exactly it. So `https://host/signin-oidc/{fragmentId}` per client and one shared
   `https://host/signin-oidc` are both one line in `callback_urls`. The constraint is the gateway's:
   `OidcOptionsSetup.CallbackPath` is the fixed `/signin-oidc`, `ShellGate` reserves it, and the ID token's
   `aud` is the client id ([access token][at]), so the callback must be handled by the scheme of the client
   that issued the code or ID-token validation fails. That is the dispatch question the map reserved for
   the prototype; this finding only says the provider accepts either answer.
5. **`client_id` is the claim that tells clients apart; `aud` tells resources apart.** The access token
   always carries `client_id`, "the user pool app client that authenticated your user", and `aud` "only
   if your application requested a resource binding", set to the `resource` URL of the authorization
   request; `scope` is a space-delimited list ([access token][at], [resource binding][rs]). The ID
   token's `aud` is the client id ([access token][at]). Observed: app1, which sends `resource`, got
   `aud=https://orders.spa-poc.test`; app2, which does not, got no `aud`; both got `client_id` (C-2.7).
   Two fragments sending the same `resource` get the same `aud`, so the negative test the map wants, "a
   fragment's API rejects a token issued to another client of the same pool", must check `client_id`,
   which is what `fixtures/MockApi/ClientAdmission.cs` already does. Any client may be authorized for
   any resource server's scopes: "you can authorize any app client in your user pool to issue custom
   scopes from any of your resource servers" ([resource servers][rs]); 50 scopes per client, 100 per
   resource server, 100 resource servers per pool (adjustable to 300) ([quotas][quotas]). The developer
   guide contradicts itself on a requested scope the client is not associated with: "ignored at runtime"
   on the authorize page, "authentication fails" on the resource-server page, and `invalid_scope` among
   the authorize page's negative responses. The apps root's precondition makes it moot here.
6. **Revocation is N calls, logout is one.** A revocation "must include the client ID that was used to
   obtain the token" and "doesn't affect any of the user's other refresh tokens"
   ([token revocation][rev]); a client with a secret authenticates with `Basic
   base64(client_id:client_secret)` ([revoke endpoint][revoke]), as ADR-0017 found. So one gateway
   logout revokes each token set with its own client's secret, N requests. A revoked token still
   verifies by signature and expiry in any JWT library ([token revocation][rev]), so an API that checks
   only those accepts it until `exp`, five minutes here. `/logout` takes one `client_id` and a
   `logout_uri` from that client's sign-out URLs ([logout][logout]); it clears the one domain cookie, so
   it ends the provider's session for every client in that browser (C-2.14 observed the next login ask
   for a password; the knowledge centre says the endpoint "clears the session cookie" [re:Post
   logout][repost]). Revocation alone does not end silent re-auth: "because managed login session
   cookies don't expire automatically, your user can re-authenticate with a session cookie", so after
   revoking, "redirect them to the Logout endpoint" ([refresh tokens][refresh]). `GlobalSignOut` would
   revoke every token of the user in one call, but across every client and device, and it needs a live
   access token ([token revocation][rev]); not a fit for one browser's logout.
7. **What `infra/` makes per client today, and what a fragment entry would add.** The apps root makes
   three resources per app definition, `for_each` over `local.apps` keyed by `appId`: the client
   (`spa-platform-<cell>-<appId>`, confidential, code grant, the definition's `oauth.scopes`, one callback
   `https://<host>/signin-oidc`, one sign-out URL `https://<host>/`, 5-minute access and ID tokens, a
   refresh token of the registry's longest session plus 60 minutes, rotation with a 60-second grace,
   revocation on, `ALLOW_USER_SRP_AUTH` only), its default branding style, and a `SecureString`
   parameter `/spa-platform/<cell>/apps/<appId>/client-secret` re-created with the client
   (`infra/envs/poc/apps/main.tf`). Its one output, `clients`, maps `appId` to `{clientId,
   secretParameter, secretVersion}`, which is `contracts/infra-outputs.schema.json → clients`, frozen at
   G1a and keyed by `appId` ("one entry per app"). A fragment client needs: a key (a composite such as
   `<appId>-<fragmentId>` fits the `appId` pattern `^[a-z0-9-]+$`, 3 to 40 characters, but the schema
   says the key is an app, so either a composite key or a nested `fragments` map is a change request on a
   frozen contract); a flatten of `fragments[]` into `local.apps`; a parameter path; and a row in the
   test file's `existing_app_resources`, which compares every argument of every existing client. The
   `fragments[]` block itself is a change request too: `contracts/app-definition.schema.json` is
   `additionalProperties: false` and frozen at G1a. The platform root reads only
   `clients["app1"]` and `clients["app2"]`, so it is unaffected.

## 1. Silent re-authentication across clients

What the developer guide says, verbatim where it matters:

- "When a user signs in with your login pages or a third-party provider, Amazon Cognito sets a cookie in
  their browser. With this cookie, users can sign in again with the same authentication method for one
  hour. When they sign in with their browser cookie, they get fresh tokens that last the duration
  specified in your app client configuration. Changes to user attributes or authentication factors have
  no effect on their ability to sign in again with their browser cookie. Authentication with the session
  cookie doesn't reset the cookie duration to an additional hour. Users must sign in again if they attempt
  to access your sign-in pages more than an hour after their most recent successful interactive
  authentication." ([managed login][ml], "The one-hour managed login and hosted UI session cookie".)
- "For access and ID tokens, don't specify a minimum less than an hour if you use managed login. Managed
  login sets browser cookies that are valid for one hour. If you configure an access token duration of
  less than an hour, this has no effect on the validity of the managed login cookie and users' ability to
  reauthenticate without additional credentials for one hour after initial sign-in." ([access token][at].)
  The apps root sets five minutes on purpose (refresh during a test run); the note is about expectations,
  not a constraint.
- "Token refresh in custom applications doesn't affect managed login sessions. These sessions are set in a
  browser cookie and are valid for one hour. The `GetTokensFromRefreshToken` response issues new ID,
  access, and optionally refresh tokens, but doesn't renew the managed login session cookie."
  ([refresh tokens][refresh].)
- The quota table lists "Hosted UI session cookie: 1 hour" under session validity parameters
  ([quotas][quotas]).

The `prompt` parameter ([authorize][authz]): "An OIDC parameter that controls authentication behavior for
existing sessions. Available in the managed login branding version only, not in the classic hosted UI."
`prompt=none`: "Amazon Cognito silently continues authentication for users who have a valid authenticated
session. With this prompt, users can silently authenticate between different app clients in your user
pool. If the user is not already authenticated, the authorization server returns a `login_required`
error", delivered as `302` to `redirect_uri?error=login_required&state=...`. `prompt=login`: "requires
users to re-authenticate even if they have an existing session ... When a user who has an existing session
signs in again, Amazon Cognito assigns them a new session cookie." `select_account` and `consent` affect
only redirects to third-party IdPs. Omitted: "managed login follows the default behavior: users must sign
in unless their browser has a valid managed login session cookie." Values may be combined, space-delimited.

`max_age` ([authorize][authz], "Example: Step-up authentication request"): "A non-negative integer that
specifies the maximum number of seconds since the user last authenticated. If the current time minus
`auth_time` is greater than `max_age`, the user must authenticate again from scratch instead of only
stepping up. This behavior is identical to the `MAX_AGE` API parameter." The section is headed by a note
that "authentication levels with ACR and AMR claims is rolling out gradually", and `acr_values` needs the
Essentials or Plus plan (the pool is Essentials). The page documents `max_age` only beside `acr_values`;
read alone it would shorten the silent window below the hour, never lengthen it.

What was observed in this repository (not re-run): the G0 spike's second login, app2 as the same user
in the same browser after app1's interactive login, recorded "asked for a password: no; managed login
seen: no" with the hop list `https://app2.../ > .../oauth2/authorize > https://app2.../signin-oidc >
https://app2.../` (`spikes/g0/cognito/output-cognito.txt` line 50, finding C-2.6). The first login's
list has a `/login` hop between `authorize` and the callback (line 48). The cookie belongs to the
Cognito domain, not to the app's host, so two clients on one host and two clients on two hosts look the
same to the provider; that is an inference from where the cookie is set, not a documented statement.

The classic hosted UI, for completeness: the knowledge centre says the cookie is named `cognito`, lasts
one hour, and "when a user tries to sign in again during an active session, Amazon Cognito asks the user
if they want to continue their existing session ... If a user chooses the **Sign in as
example_username** button to use an existing session, then the cookie's validity resets to 1 hour"
([re:Post logout][repost]). That is a prompt, not a silent redirect, and its reset rule is the opposite
of managed login's. It is an AWS knowledge-centre article, not the developer guide. The cell's domain
has `managed_login_version = 2` (`infra/modules/idp-cognito/main.tf`), so it is managed login that
applies.

**Consequences for the map's "lazy fragment login".** A fragment route answering 401 and the SPA
navigating to `/bff/login?fragment=...` completes with no page only inside the hour after the user's
last interactive sign-in at this pool. Outside it, the user sees managed login (and any MFA) for the
fragment although the shell still has a session. Options the docs leave open, for the spec to choose:
send `prompt=none` on a fragment's authorization and treat `login_required` as "ask the user" (the
ASP.NET handler has an `OpenIdConnectOptions.Prompt` setting; how it surfaces `login_required` at the
callback is section 9's); or accept a visible prompt after the hour; or log every fragment in eagerly
inside the hour, which the map rejected as N redirects.

## 2. Redirect URIs

Provider rules ([authorize][authz], `redirect_uri`; [CreateUserPoolClient][cupc], `CallbackURLs`;
[app client settings][clients]):

- absolute; pre-registered with the client; no fragment component; HTTPS, except `http://localhost`,
  `http://127.0.0.1` and `http://[::1]` for testing; custom TCP ports allowed; custom schemes such as
  `myapp://` allowed;
- `CallbackURLs`: 0 to 100 items, each 1 to 1,024 characters, pattern `[\p{L}\p{M}\p{S}\p{N}\p{P}]+`;
  `LogoutURLs` the same shape; quota "Callback URLs per app client: 100, Logout URLs per app client: 100",
  neither adjustable ([quotas][quotas]);
- `DefaultRedirectURI` "must be in the `CallbackURLs` list" and replaces a missing `redirect_uri` only
  for a client with one assigned IdP;
- the page defers the matching rule to "OAuth 2.0 - Redirection Endpoint" and says no more. RFC 6749
  §3.1.2.3: when the full redirection URI is registered, the server "MUST compare the two URIs using
  simple string comparison" ([RFC 6749][rfc6749]). The Cognito page does not restate it, so "exact
  match" is the RFC's rule as the page points at it, not a Cognito sentence.

Nothing read ties a callback URL to one client or requires it to be unique in a pool. So, per fragment
client, `callback_urls = ["https://<host>/signin-oidc/<fragmentId>"]` is as registrable as
`["https://<host>/signin-oidc"]` repeated on every client of the host. A client may also hold both,
within 100. The G0 spike registered one URL per client and sent exactly that one (C-3.1 to C-3.5 passed
with a port in the URL).

What the gateway imposes today: `OidcOptionsSetup.CallbackPath` is the one `PathString("/signin-oidc")`
for every scheme, `ShellGate` lists `/signin-oidc`, `/signout-callback-oidc` and `/signout-oidc` as
reserved, and `contracts/http-contract.md` names them so. A fragment client's code arrives at whichever
callback is registered, and the ID token that comes back from the code exchange has `aud` equal to that
client's id ([access token][at]: `client_id` is "the same value in the ID token `aud` claim"), so the
scheme that validates it must be the fragment's. Whether that is one path per fragment or one shared path
with dispatch on `state` is the prototype's; the provider is indifferent.

Terraform (`aws_cognito_user_pool_client`, [provider v6.68.0][tf-client]): `callback_urls` and
`logout_urls` are optional lists, usable once `allowed_oauth_flows_user_pool_client = true`;
`default_redirect_uri` "must be included in the list of callback URLs". A change to `callback_urls` is an
in-place update (the apps root's parameter re-creates only on the client's id, `#554`).

## 3. Scopes, resource servers and the access token's claims

- Resource servers and scopes ([resource servers][rs]): a scope's full name is
  `resourceServerIdentifier/scopeName`; "you can authorize any app client in your user pool to issue
  custom scopes from any of your resource servers"; deleting a scope marks it inactive rather than
  removing its client associations. Two clients sharing `https://orders.spa-poc.test/read` is the normal
  case, and the fixtures already give app1 and app2 scopes from the same two resource servers.
- Per client ([CreateUserPoolClient][cupc], `AllowedOAuthScopes`; [authorize][authz], `scope`): the
  client's allowed list bounds what it may request; "if the client doesn't request any scopes, the
  authentication server uses all scopes that are associated with the client"; `openid` is needed for an
  ID token; `profile`, `email` and `phone` need `openid`. A requested scope the client is not associated
  with: "will be ignored at runtime" ([authorize][authz], `scope`), versus "authentication fails"
  ([resource servers][rs], "About resource servers"), versus `error=invalid_scope` for "scope that is
  unknown, malformed, or not valid" ([authorize][authz], negative responses). The three statements are
  not reconciled on the pages; the apps root's `precondition` refuses a definition whose scope no
  resource server offers, and Terraform refuses a scope the pool does not know, so the gateway never asks
  for one.
- Quotas ([quotas][quotas]): scopes per app client 50; scopes per resource server 100; resource servers
  per user pool 100, adjustable to 300.
- The access token ([access token][at]): `client_id`, "the user pool app client that authenticated your
  user"; `aud`, "the URL of the API that the access token is intended to authorize. Present only if your
  application requested a resource binding"; `scope`, "a list of OAuth 2.0 scopes issued to the signed-in
  user", space-delimited in the sample; `origin_jti` and `jti` once revocation is on; `token_use:
  access`; `username`, `sub`, `cognito:groups`.
- Resource binding ([resource binding][rs]; [authorize][authz], `resource`): RFC 8707; the value must be
  a URL (`https://`, `http://localhost` or a custom scheme); one resource per request; "exclusive to
  managed login authentication"; "token-refresh grants from the Token endpoint carry over the `aud`
  claim from the original request" (observed as C-2.9). If the resource is a user pool resource server,
  its identifier must be URL-shaped, which `fixtures/cognito/pool.json`'s are.
- Observed (C-2.7, 2026-10-16): app1 sent `resource` and got `aud=https://orders.spa-poc.test`,
  `client_id` app1's, `scope=openid profile https://orders.spa-poc.test/read`; app2 sent none and got no
  `aud`, `client_id` app2's.

**Refusing a token issued to another client.** `aud` carries the resource, so two fragments that both
set `resource: https://orders.spa-poc.test` get indistinguishable `aud` values; a fragment that sets no
`resource` gets none. `client_id` is set on every access token and is the per-client value. The mock
API's admission already reads `client_id` from the validated principal and admits a listener's one
client (`fixtures/MockApi/ClientAdmission.cs`, `Routes.ClientOf`); the platform root feeds it the client
ids of apps 1 and 2 from the apps root's state (`infra/envs/poc/platform/main.tf`). The map's new
negative test is therefore the existing mechanism with a fragment's client id in another listener's
allowlist, or absent from it. An API that validates `aud` only would also need each fragment to bind a
distinct `resource`, which `oauth.resource` allows but does not force (one URL per `oauth` block).

## 4. Revocation and logout, per client

**`/oauth2/revoke`** ([revoke endpoint][revoke]): `POST`, `application/x-www-form-urlencoded`, body
`token=<refresh token>`; "if your app client has a client secret, the application must pass its
`client_id` and `client_secret` in the authorization header through Basic HTTP authorization"; the body's
`client_id` is required only for a public client. Errors: `400 invalid_request` when the token is missing
or revocation is off for the client; `400 unsupported_token_type` when the token is not a refresh token;
`401 invalid_client` for bad credentials; `200 OK` for a token already revoked or invalid, so a second
revocation is idempotent. ADR-0017 recorded the form-body refusal and the Basic acceptance (C-2.19), and
`IdpProfiles.cs` holds the Cognito profile to Basic.

**Scope of a revocation** ([token revocation][rev]): "When you revoke a refresh token, all access tokens
that were previously issued by that refresh token become invalid. The other refresh tokens issued to the
user are not affected." "Your request to revoke a refresh token must include the client ID that was used
to obtain the token." Revocation is per client in an app client's settings (`EnableTokenRevocation`,
default on; `enable_token_revocation` in Terraform) and revoking a rotated-out token reaches its successor
(C-2.19). Hence the map's logout is one revocation per token set, each with its own client's secret, and a
failure on one does not touch another. The `UserToken` category (`RevokeToken`) allows 120 requests per
second per Region, adjustable ([quotas][quotas]).

**What a revoked token still does**: "revoked tokens will still be valid if they are verified using any
JWT library that verifies the signature and expiration of the token" ([token revocation][rev]). An API
that trusts signature and `exp` alone accepts a revoked access token until it expires: five minutes with
the apps root's validity.

**The whole-user alternatives** ([token revocation][rev]; [refresh tokens][refresh]): `GlobalSignOut`,
authorized by the user's own access token, and `AdminUserGlobalSignOut`, by IAM, revoke "all of the
requesting user's refresh, ID, and access tokens", every client and device at once. Either ends more than
one browser's session and the first needs a valid access token; neither is the shape of one browser's
logout.

**`/logout`** ([logout][logout]): `GET`; `client_id` required; either `logout_uri`, which "must be one of
the Allowed sign-out URLs for your app client", or `redirect_uri` plus the parameters of a fresh
authorization request, in which case the user is sent on to `/login`. The endpoint "signs out the user";
the knowledge centre states the mechanism, "Amazon Cognito clears the session cookie" ([re:Post
logout][repost]). The cookie is the domain's, not the client's, so the provider's session ends for every
client in that browser: observed as C-2.14, where the next login to app1 after app1's logout asked for a
password (2026-10-16). Cognito returns no `state` from `/logout` (`R-C7`), and the endpoint "doesn't sign
users out of OIDC or social identity providers". So in the map's one logout, after N revocations, one
redirect to `/logout` with the shell's `client_id` and the shell's registered `logout_uri` is enough; the
fragments' clients need no `logout_urls` of their own for that path, and the gateway builds exactly that
redirect today (`IdpProfiles.cs`: `client_id` and `logout_uri` on `BrowserOrigin/logout`).

**Revocation without logout does not end silent re-auth**: "the user must re-authenticate to get new
tokens. Because managed login session cookies don't expire automatically, your user can re-authenticate
with a session cookie, with no additional prompt for credentials. After you sign out your managed login
users, redirect them to the Logout endpoint, where Amazon Cognito clears their session cookie"
([refresh tokens][refresh]). This is why `docs/runbooks/end-app-sessions.md` says its purge neither
revokes nor ends the provider session, and why the map's "revoke every set, then one provider logout" has
both halves.

## 5. Quotas

From [quotas][quotas], "Amazon Cognito user pools resource quotas", read 2026-11-03:

| Resource | Default | Adjustable | Maximum |
| --- | --- | --- | --- |
| App clients per user pool | 1,000 | Yes | 10,000 |
| Managed login branding styles per user pool | 20 | No | n/a |
| Callback URLs per app client | 100 | No | n/a |
| Logout URLs per app client | 100 | No | n/a |
| Scopes per app client | 50 | No | n/a |
| Scopes per resource server | 100 | No | n/a |
| Resource servers per user pool | 100 | Yes | 300 |
| Identity providers per user pool | 300 | Yes | 1,000 |

Session validity: access and ID tokens 5 minutes to 1 day; refresh token 1 hour to 3,650 days; "Hosted UI
session cookie: 1 hour". Secrets: "each app client can have up to two secrets at a time"
([app client settings][clients]), which `docs/runbooks/secret-rotation.md` relies on.

Rates that a shell with N fragments touches ([quotas][quotas]): `UserAuthentication` 120 requests per
second per Region (adjustable), where "token redemption in authorization-code grants is subject to an
additional quota allocation at the same rate"; `UserToken` (`RevokeToken`) 120; per domain, 300 requests
per second from one source IP, 300 for one app client id, 500 for the domain, none adjustable;
`UserPoolClientUpdate` (`CreateUserPoolClient` and friends) 15 per second and at most 5 per second on one
pool, which bounds how fast an apply with many new clients runs, not whether it runs.

**The binding one is styles.** The apps root creates `aws_cognito_managed_login_branding` per client
because, by API, a client has no managed login pages without one ([`ManagedLoginVersion`][mlv]: "when you
create an app client programmatically, your app client has no branding style. To use managed login, create
a branding style"), and a style is bound to one `ClientId` for its life ([CreateManagedLoginBranding][cmlb];
[branding][branding]: "you can't change the assigned styles of an app client"). Twenty styles per pool,
not adjustable, is twenty clients that can show a managed login page in the cell. Every fragment client
counts against the same twenty as the shell and as every other app in the cell. That is the number the
map's "how many fragments a shell may carry" meets first, before the session record's size.

## 6. What `infra/` creates per client today

`infra/envs/poc/apps/main.tf` (task 1.8), read at `e7ffb47`:

- `local.apps`: every `*.json` under `definition_dirs` (default `fixtures/apps/`), decoded and keyed by
  `appId`; `local.grantable_scopes`: the five Cognito scopes plus every resource server's full scope names
  from the identity root's state; `local.refresh_token_minutes`: `registry.schema.json`'s
  `session.lifetimeMinutes.maximum` plus 60.
- `aws_cognito_user_pool_client.app[appId]`: name `spa-platform-<cell>-<appId>`; `generate_secret`;
  `allowed_oauth_flows = ["code"]`; `allowed_oauth_scopes = oauth.scopes`; `supported_identity_providers =
  ["COGNITO"]`; `callback_urls = ["https://<host>/signin-oidc"]`; `logout_urls = ["https://<host>/"]`;
  access and ID tokens 5 minutes; refresh token `local.refresh_token_minutes`; `refresh_token_rotation {
  ENABLED, 60 }`; `enable_token_revocation`; `prevent_user_existence_errors`; `explicit_auth_flows =
  ["ALLOW_USER_SRP_AUTH"]`; a `precondition` that every scope is grantable.
- `aws_cognito_managed_login_branding.app[appId]`: `use_cognito_provided_values`, bound to the client.
- `aws_ssm_parameter.client_secret[appId]`: `/spa-platform/<cell>/apps/<appId>/client-secret`,
  `SecureString`, value ignored after creation, re-created when the client's id changes.
- Output `clients`: `{ appId => { clientId, secretParameter, secretVersion } }`, the shape of
  `contracts/infra-outputs.schema.json → clients` ("one entry per app that has an OAuth client, keyed by
  appId"; `propertyNames` is the registry's `appId`, `^[a-z0-9-]+$`, 3 to 40). The platform root reads
  `clients["app1"].clientId` and `clients["app2"].clientId` for the mock API.
- `tests/root.tftest.hcl` holds a map `existing_app_resources` naming every argument of the three
  resources per app and fails when `main.tf` sets an argument the map does not name, so any new argument
  (a second callback URL, say) is a change to both files.

**The fragment entry's Terraform shape, as the facts above bound it.** A fragment's client is the same
resource with the same arguments and a different `callback_urls` line; its style is the same resource
and counts against the twenty; its parameter needs a path, for which `/spa-platform/<cell>/apps/<appId>/
fragments/<fragmentId>/client-secret` keeps the task role's prefix. The flatten is `for app in apps, for f
in try(app.fragments, [])`. What the shape cannot do without a change request: `fragments[]` on an app
definition (schema `additionalProperties: false`, frozen at G1a), and a fragment's row in the outputs'
`clients` (keyed by app, frozen at G1a). The refresh-token validity stays per client; a fragment's token
set expires on its own clock inside the one record, as the map already decided.

## 7. What this does not settle

- How the gateway dispatches a fragment's callback (one path per fragment or one shared path): the
  prototype's.
- Whether to send `prompt=none` for a fragment and fall back on `login_required`: a spec decision; the
  provider supports it on managed login only.
- Per-client secrets for S9's `private_key_jwt` and per-fragment rotation: not in these pages.
- Pricing: app clients, styles and parameters add nothing (`docs/plan/05-infrastructure-and-cost.md`);
  managed login needs Essentials, which the pool has.

## 8. What I executed and what I read

**Executed** (this session, 2026-11-03), none of it against AWS:

- `gh issue edit 767 --add-assignee @me`; `gh issue view 767`, `765`, `584`.
- `git fetch origin`, `git worktree add` on `origin/main` `e7ffb47`; `git show
  origin/research/codeql-csharp:docs/research/codeql-csharp-for-this-solution.md` for the format.
- Reads inside the worktree with `cat`, `grep`, `sed` and `python3 -I` on `infra/`, `contracts/`,
  `docs/adr/0017-*.md`, `docs/runbooks/`, `fixtures/`, `spikes/g0/cognito/`, `src/Platform.Identity/`.
- `curl` to `raw.githubusercontent.com/hashicorp/terraform-provider-aws` for the three resource pages at
  `v6.68.0` (the apps root's lock) and at `main`; the two are byte-identical.
- The AWS documentation tools (`search_documentation`, `read_documentation`), which fetch public pages
  from docs.aws.amazon.com and repost.aws. They are documentation reads only; no `run_script`, no
  `aws` CLI, no Terraform, no credential, no cell read. Pages longer than the tool's output limit were
  saved by the tool and sliced locally with `python3 -I`.

**Read only:** every provider fact above. **Observed earlier, cited and not re-run:** C-2.6, C-2.7,
C-2.9, C-2.14, C-2.19 and C-3.x from `spikes/g0/cognito/output-cognito.txt` (2026-10-16, the original
pool, managed login version 2, two hosts in one pool). **Not done:** no apply, no plan, no credential
step, no `describe-user-pool-client`, no browser against the cell.

## 9. Unverified: what only a live check settles

1. Whether a client **without a branding style** completes a cookie-only authorization that renders no
   page, or fails as a page would. Bears on whether the twenty-style quota binds fragments that are only
   ever logged in silently. Reading says treat it as binding.
2. Whether `max_age` on `/oauth2/authorize` acts **without `acr_values`** on an Essentials pool, and
   whether it is live in the cell's Region, given the "rolling out gradually" note.
3. The exact **comparison rule** for `redirect_uri` against `CallbackURLs` (case, trailing slash, query):
   the Cognito page defers to RFC 6749 and the spike exercised only an exact repeat.
4. How the ASP.NET handler surfaces `error=login_required` from a `prompt=none` request: expected as a
   remote failure at the callback, not read here.
5. That two clients in one pool may register the **same callback URL**: no text forbids it and nothing
   read asserts it; an apply would show it in seconds.
6. The three-way wording on an **unassociated scope** (ignored, fails, `invalid_scope`).
7. Any **rate** effect of N code exchanges and N revocations per user action; the published quotas are
   far above a test cell's use.

[ml]: https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-managed-login.html
[authz]: https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html
[logout]: https://docs.aws.amazon.com/cognito/latest/developerguide/logout-endpoint.html
[revoke]: https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html
[rev]: https://docs.aws.amazon.com/cognito/latest/developerguide/token-revocation.html
[refresh]: https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-refresh-token.html
[at]: https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-access-token.html
[rs]: https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-define-resource-servers.html
[clients]: https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-settings-client-apps.html
[quotas]: https://docs.aws.amazon.com/cognito/latest/developerguide/quotas.html
[branding]: https://docs.aws.amazon.com/cognito/latest/developerguide/managed-login-branding.html
[cupc]: https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_CreateUserPoolClient.html
[cmlb]: https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_CreateManagedLoginBranding.html
[rtok]: https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_RevokeToken.html
[mlv]: https://docs.aws.amazon.com/sdk-for-ruby/v3/api/Aws/CognitoIdentityProvider/Types/CreateUserPoolDomainRequest.html
[repost]: https://repost.aws/knowledge-center/cognito-logout-endpoint-globalsignoutapi
[rfc6749]: https://www.rfc-editor.org/rfc/rfc6749#section-3.1.2.3
[tf-client]: https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/cognito_user_pool_client.html.markdown
[tf-rs]: https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/cognito_resource_server.html.markdown
[tf-branding]: https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/cognito_managed_login_branding.html.markdown
