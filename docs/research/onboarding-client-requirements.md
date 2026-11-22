# What an app's OAuth client has to be at the provider

- **Ticket:** #1478, "What an app's OAuth client has to be at the provider: every requirement the
  gateway places on it, stated or implied", on the map #1477, "Wayfinder: app onboarding, an
  adopter's operator onboarding an app in its own estate with any identity provider".
- **Date:** 2026-11-22
- **Branch:** `research/onboarding-client-requirements`, from `origin/main` at `4035fe95`. Every
  path:line below is at that commit.
- **Method:** read only. Sources: `infra/envs/poc/apps/main.tf`; `src/Platform.Identity`
  (`OidcOptionsSetup.cs`, `IdpProfiles.cs`, `CredentialStyles.cs`, `CodeExchange.cs`,
  `ProfileTokenEndpoints.cs`, `AccessTokenSource.cs`, `TokenSet.cs`, `SessionEnd.cs`, `Bff.cs`);
  `src/Platform.Abstractions` (`IIdpProfile.cs`, `ClientCredentialStyle.cs`);
  `fixtures/TestIdp`, `fixtures/MockApi`, `tests/Gateway.TestHarness/GatewayHarness.cs`;
  `contracts/` (`http-contract.md`, `upstream-contract.md`, `platform-config.schema.json`,
  `registry.schema.json`, `infra-outputs.schema.json`, `changes/p2-6-pingfederate-profile.md`);
  ADR-0003, 0005, 0011, 0017, 0024, 0028; `docs/plan/01-plan.md`, `03-workstreams-and-tasks.md`,
  `05-infrastructure-and-cost.md`; the decision comment on the continuity spec (#332, D1); and one
  page of Ping's public documentation (PingFederate 13.1, "Configuring OAuth clients"). Nothing was
  built, run or applied, and no PingFederate was used.

Labels. **Stated**: a contract, an ADR, the plan or a decided change request says it. **Implied**:
only Terraform, code or a test shows it. A third word is used where the evidence demands it:
**tolerated**, a property the gateway copes without, so it is not a requirement.

## Answer in brief

1. **The client is confidential, uses the authorization code grant with PKCE (S256), and allows
   the refresh grant.** The gateway always sends `response_type=code`, `response_mode=query` and an
   S256 challenge, and presents a client credential on the code exchange, the refresh and the
   revocation (stated: `docs/plan/01-plan.md:51`, `:300` A4; `ClientCredentialStyle.cs:8-18`).
   **The login's token response must carry a refresh token.** Without one the shell's session ends
   at the first access-token expiry (implied: `AccessTokenSource.cs:282-293`). The gateway asks for
   exactly the definition's `oauth.scopes` and adds no `offline_access` (`OidcOptionsSetup.cs:77-81`),
   so on a provider that issues refresh tokens only for `offline_access` the definition must list
   it. No fixture does today.
2. **Client authentication is a client secret, in the form body (`standard`) or a Basic header
   (`cognito`), and the client must accept that one style at both the token and the revocation
   endpoints** (stated: ADR-0017 decision 2; `IdpProfiles.cs:22`, `:50`). `private_key_jwt` is
   decided for a `pingfederate` profile (P2-6 decision items 2 to 4) but **not built at origin/main**.
   The `Profile` enum is `standard` and `cognito` only (`platform-config.schema.json:65-74`), and a
   contracts test refuses `pingfederate` (`tests/Contracts.Tests/PlatformConfigurationTests.cs:145`).
   An adopter with any provider other than Cognito runs `standard` today.
3. **Redirects, all on the app's `host` exactly as the registry entry has it, port included**
   (stated: `docs/plan/01-plan.md:309`). The redirect URI is `https://{host}/signin-oidc` (stated:
   `http-contract.md:26-27`; implied for the exact form: `OidcOptionsSetup.cs:28`, `main.tf:99`). Each
   fragment's client registers `https://{host}/signin-oidc/{fragmentId}` and no logout URL (stated:
   `http-contract.md:80`; implied: `main.tf:205-206`). The logout return differs by profile.
   `cognito` registers `https://{host}/` as a `logout_uri` (stated: plan row 3.7; `main.tf:103`).
   `standard` registers `https://{host}/signout-callback-oidc` as a `post_logout_redirect_uri`
   (implied only: the handler's default signed-out callback, and the harness registering it,
   `GatewayHarness.cs:707-711`). The back-channel logout URI `https://{host}/signout-backchannel-oidc`,
   with `backchannel_logout_session_required` false, is decided (ADR-0028 decision 1 and
   consequences) and **not built**: the path is in no file under `src/`.
4. **Lifetimes.** The refresh token must outlive the longest session the registry allows, 720
   minutes (stated: plan row 1.8, `registry.schema.json` `session.lifetimeMinutes.maximum`). The
   proof cell adds one hour, which only Terraform shows (implied: `main.tf:73-77`). The access token
   must live well over one minute, because the gateway refreshes one minute before expiry (implied:
   `AccessTokenSource.cs:96-99`). Five minutes is a proof-cell choice so that refresh happens during
   a run (stated as a choice: `docs/plan/01-plan.md:278`), not a requirement. Refresh tokens must fit
   the token library's 4,096-character limit (stated: ADR-0005:36). The ID token needs no lifetime
   beyond the callback, with one open point under `standard`: its logout sends the stored ID token
   as `id_token_hint`, which may have expired by then (point 4 of "Lifetimes").
5. **Rotation and its grace are tolerated, not required.** The gateway keeps the old refresh token
   when the provider returns none (implied: `TokenSet.cs:74-77`), and serialises refreshes per
   session across tasks (ADR-0017 decision 1, ADR-0024). The grace window only buys recovery when a
   refresh holder dies after rotation (ADR-0024 decision 4; #332 D1). The plan row says 10 seconds
   and Terraform sets 60 after #332 D1 (`docs/plan/03-workstreams-and-tasks.md:99` against
   `main.tf:118-129`). Recovery needs a grace longer than the lease's 21 s, so 10 s gives none; that
   is accepted. On Cognito, rotation is what rules `ALLOW_REFRESH_TOKEN_AUTH` out (stated: plan row 1.8).
6. **Scopes and audience.** Every scope in `oauth.scopes` must be grantable to the client, and
   `openid` is always among them (stated: `registry.schema.json` `scopes`). If `oauth.resource` is
   set, the provider must bind the access token's `aud` to it (stated: `registry.schema.json`
   `resource`; `upstream-contract.md:15`). The access token must be a JWT an upstream can validate:
   signature, `iss`, `exp`, a `client_id` the API admits, the route's scope, `aud` where bound, and
   for Cognito `token_use=access` (stated: `upstream-contract.md:15`; implied in the mock API:
   `fixtures/MockApi/MockApiHost.cs:125-150`). The ID token must carry `sub`; `name` and `email` may
   be absent (stated: `http-contract.md:15`).
7. **What the gateway reads back.** The gateway reads back:
   - the client id, unique across every app and fragment (`registry.schema.json` `clientId`; the
     validator's `DuplicateClientId`);
   - the secret, as a Parameter Store SecureString named by `ssm:{name}:{version}` and resolved
     before a snapshot goes live (ADR-0003; `docs/plan/01-plan.md:146` D3);
   - an issuer entry in `Issuers`, with `Authority`, `Profile` and `BrowserOrigin` (all stated:
     `platform-config.schema.json:50-80`).

   `spactl app add` takes the client id and the secret parameter and version from
   `outputs.clients[appId]`, and refuses if that entry is missing (`infra-outputs.schema.json:62-79`;
   `tools/spactl/Commands.cs:248`). A rotation without downtime needs the provider to hold two
   active secrets per client at once (stated: `05-infrastructure-and-cost.md:104-111`).
8. **Where the requirements are written.** Most of the hard requirements are stated somewhere, but
   in pieces across the plan, the contracts and the ADRs. Three are only implied:
   - the `standard` profile's post-logout redirect URI;
   - the refresh token at login, and `offline_access` with it;
   - the access token living longer than one minute.

   No one document lists them per client. The Cognito apps root's comments are the closest thing
   to that list, and they are Cognito-shaped.

## Grant and client authentication

| Requirement | Label | Citation | Notes |
| --- | --- | --- | --- |
| Confidential client, one per app (and one per fragment) | stated | `docs/plan/01-plan.md:300` (A4); ADR-0038; `ClientCredentialStyle.cs:8-13` | The gateway, not the SPA, is the client. |
| Authorization code grant, `response_type=code` | stated | `docs/plan/01-plan.md:51`; `OidcOptionsSetup.cs:68` | |
| `response_mode=query` (no `form_post`) for `standard` and `cognito` | stated | `docs/plan/01-plan.md:51`; `OidcOptionsSetup.cs:65-69` | `pingfederate` would use `form_post` (P2-6 decision item 5), not built. |
| PKCE with S256 on every login | stated | `ClientCredentialStyle.cs:15-18`; `docs/plan/01-plan.md:213` (T13); `OidcOptionsSetup.cs:70` | TestIdp refuses a login without S256 (`TestIdentityProvider.cs:334`). |
| Refresh grant allowed | stated | plan row 1.8 (`03-workstreams-and-tasks.md:99`); `http-contract.md` Token sets | |
| A refresh token issued at login | implied | `AccessTokenSource.cs:282-293`; TestIdp always issues one on the code grant (`TestIdentityProvider.cs:404-407`) | No refresh token means the session ends at the first expiry. |
| Credential style: form body (`standard`) or Basic header (`cognito`), the same at token and revocation | stated | ADR-0017 decision 2; `IdpProfiles.cs:22`, `:50`; `CodeExchange.cs:18-20`; `ProfileTokenEndpoints.cs:42-56` | An unregistered profile name falls back to form body (`CredentialStyles.cs:15-16`). |
| `private_key_jwt`, the gateway's public key registered on each client, pushed authorization required | stated, not built | `contracts/changes/p2-6-pingfederate-profile.md` decision items 2, 3 and 7 (lines 325-342, 343-350, 370-378), and "Blocking" (lines 294-299) | Under it `clientSecretRef` still resolves and is never sent (decision item 4, lines 351-352). |
| A revocation endpoint in discovery | tolerated | `SessionEnd.cs:202-204` | Missing, logout records `no_revocation_endpoint` and still removes the session. The Cognito client must enable revocation (`main.tf:131-132`). |

## Redirects

| Requirement | Label | Citation |
| --- | --- | --- |
| Callback `https://{host}/signin-oidc`, same path on every host | stated | `http-contract.md:26-27`; `docs/plan/02-contracts.md:242`; exact form implied by `OidcOptionsSetup.cs:27-28`, `main.tf:96-99` |
| The host as the registry has it, port included (`app1.spa-poc.test:8443` in the proof cell) | stated | `docs/plan/01-plan.md:309`; G0 item 3 (`01-plan.md:104`); ADR-0017 context (C-3.1 to C-3.5) |
| Fragment callback `https://{host}/signin-oidc/{fragmentId}`, no logout URL on the fragment's client | stated | `http-contract.md:80`; ADR-0038; `OidcOptionsSetup.cs:85-92`; `main.tf:202-206` |
| `cognito` logout return `https://{host}/` as `logout_uri` | stated | plan row 3.7 (`03-workstreams-and-tasks.md:128`, `:134`); ADR-0011; `IdpProfiles.cs:64-77`; `main.tf:101-103` |
| `standard` logout return `https://{host}/signout-callback-oidc` as `post_logout_redirect_uri` | implied | handler default; `IdpProfiles.cs:9-15`; harness registers both returns (`GatewayHarness.cs:707-711`); TestIdp refuses an unregistered one (`TestIdentityProvider.cs:508`) |
| Back-channel logout URI `https://{host}/signout-backchannel-oidc`, `backchannel_logout_session_required` false | stated, not built | ADR-0028 decision 1 (line 29-31) and consequences (line 74) |

## Lifetimes

1. **Refresh validity.** It must outlive the longest session the registry allows, so a session
   never ends early (stated: plan row 1.8; `registry.schema.json` `session.lifetimeMinutes`, maximum
   720). The proof cell sets 720 + 60 minutes, reading the maximum from the contract (implied:
   `main.tf:73-77`, `:110`). The session has no sliding expiry (stated: `registry.schema.json`
   `session`). Whether a provider's refresh expiry is absolute or idle is the provider's own
   behaviour. PingFederate's equivalent settings, by name only, are "Persistent Grants Max Lifetime"
   and "Persistent Grants Idle Timeout" (Ping documentation, below).
2. **Access-token validity.** It must be comfortably longer than the one-minute refresh-ahead
   window (implied: `AccessTokenSource.cs:96-99`). Five minutes is a test choice (stated:
   `01-plan.md:278`; `main.tf:105-109`); TestIdp uses the same (`TestIdpFaults.cs:19`).
3. **Refresh-token size.** At most 4,096 characters, the library's limit (stated: ADR-0005:36;
   `01-plan.md:101`).
4. **ID token.** It is read at the callback (`main.tf:105-107`). A refresh's ID token replaces the
   stored one only when the answer carries one (implied: `TokenSet.cs:64-82`, T-B13). Under
   `standard`, logout sends the stored ID token as `id_token_hint` (`Bff.cs:169-177`). With
   five-minute ID tokens and a session of up to 720 minutes, that hint is often expired. OpenID
   Connect RP-Initiated Logout 1.0 asks a provider to accept an expired ID token as a hint, but
   whether a given provider does is **open**: P2-6 decision item 8 lists RP-initiated logout against
   a real `end_session_endpoint` as a fact for the deployed run, and no code here settles it.
5. **Rotation and grace.** Rotation is tolerated, not required. Without rotation the presented
   refresh token is kept (`TokenSet.cs:74-77`), and TestIdp can be switched to that mode
   (`TestIdpFaults.cs:30`). With rotation the gateway's per-session gate and cross-task lease keep
   one refresh at a time (ADR-0017 decision 1; ADR-0024). The grace window then only gives recovery
   for a holder that died between rotation and its write (ADR-0024 decision 4; #332 D1). That needs
   a grace longer than the lease's 21 s; at 10 s the window is empty, which #332 accepted. Values:
   the plan row says 10 s (`03-workstreams-and-tasks.md:99`); the cell runs 60 s since #332 D1 and
   #339 (`main.tf:118-129`); TestIdp defaults to 10 s (`TestIdpFaults.cs:26`); the two-host proofs
   run at zero (ADR-0024 consequences). The charting note on the map calls the grace a requirement;
   the evidence makes it a recovery aid.
6. **Cognito only.** `ALLOW_REFRESH_TOKEN_AUTH` stays off because rotation rules it out (stated:
   plan row 1.8; `main.tf:135-138`). SRP is the one direct flow left on, which the gateway never
   uses (proof-cell choice).

## Scopes and audience

| Requirement | Label | Citation |
| --- | --- | --- |
| Every `oauth.scopes` value grantable to the client; `openid` among them | stated | `registry.schema.json` `$defs/scopes`; Cognito precondition `main.tf:140-145` (implied) |
| The gateway sends exactly those scopes and no others | implied | `OidcOptionsSetup.cs:77-81` |
| `offline_access` when the provider requires it for a refresh token | implied | follows from the two rows above and `AccessTokenSource.cs:282-293`; PingFederate has a client setting "Require Scope Offline_Access to Issue Refresh Tokens" (Ping documentation, below) |
| `oauth.resource`, when set, bound into the access token's `aud`; kept across a refresh that sends no `resource` | stated | `registry.schema.json` `$defs/resource`; ADR-0017 context (C-2.7, C-2.9); `OidcOptionsSetup.cs:83` |
| Access token a JWT the API validates: signature, `iss`, `exp`, `client_id` on its allowlist, the route's scope, `aud` where bound, Cognito's `token_use=access` | stated | `upstream-contract.md:15`; mock API `MockApiHost.cs:125-150` (implied); P2-6 decision item 8 recommends JWT access tokens for PingFederate |
| ID token with `sub`; `name` and `email` optional | stated | `http-contract.md:15` |
| One audience per app | stated | `01-plan.md:300` (A4); `upstream-contract.md` "One token per app" |

## What the gateway reads back

- **Client id.** One string of printable ASCII with no spaces, unique across every app and fragment
  (stated: `registry.schema.json` `$defs/clientId`; validator rule `DuplicateClientId`,
  `src/Platform.Registry/RegistryValidator.cs:263-270`).
- **The secret's reference and version.** `ssm:{name}:{version}`, a SecureString read with
  decryption, pinned to that version, and resolved before a snapshot goes live (stated: ADR-0003;
  `01-plan.md:146` D3; implied in `src/Platform.Registry/ParameterStoreClientSecretSource.cs:104-111`).
  A rotation raises the version and the entry's `clientSecretRef` follows
  (`infra-outputs.schema.json:79`). It needs two active secrets on the client at once (stated:
  `05-infrastructure-and-cost.md:104-111`).
- **Where `spactl` finds them.** `outputs.clients[appId]` and `.fragments[fragmentId]` carry
  `clientId`, `secretParameter` and `secretVersion` (stated: `infra-outputs.schema.json:62-100`).
  `app add` refuses with "Apply the apps root" when the entry is missing
  (`tools/spactl/Commands.cs:248`).
- **The issuer.** The entry's `oauth.issuer` names a key of `Issuers`, which holds `Authority` (https,
  or loopback http), `Profile` (`standard` or `cognito`) and `BrowserOrigin` (stated:
  `platform-config.schema.json:50-80`; ADR-0011). `Issuers` is read at start-up (ADR-0042), so a
  new issuer is not restart-free. A fragment's issuer must equal its shell's (`registry.schema.json`
  `$defs/fragment`, `FragmentIssuerMismatch`).

## Where the profiles differ

The profiles in code are `standard` and `cognito`. `pingfederate` exists only as the decided change
request P2-6. TestIdp is a provider, not a profile: the harness runs it under `standard` by default
(`GatewayHarnessOptions.cs:27`) and can emulate Cognito's logout.

| Property | `standard` (any OIDC provider today) | `cognito` | `pingfederate` (decided, not built) | TestIdp (the hermetic provider) |
| --- | --- | --- | --- | --- |
| Credential style | form body (`IdpProfiles.cs:22`) | Basic header (`IdpProfiles.cs:50`) | `private_key_jwt`, one gateway key per cell per issuer (P2-6 items 2, 3) | accepts `client_secret_basic` and `client_secret_post` (`TestIdentityProvider.cs:294`) |
| Response mode | `query` | `query` | `form_post` (P2-6 item 5) | `query` only (`TestIdentityProvider.cs:292`) |
| Pushed authorization | handler default, used if advertised | same | required (P2-6 item 7) | none |
| Logout | RP-initiated, `post_logout_redirect_uri` `/signout-callback-oidc`, then `/` | `GET /logout` on `BrowserOrigin` with `client_id` and `logout_uri` `https://{host}/` (`IdpProfiles.cs:64-77`) | RP-initiated, as `standard` (P2-6 item 6) | serves both styles, refuses unregistered returns (`TestIdentityProvider.cs:508`, `:534`) |
| Back-channel logout | ADR-0028, not built; profile-blind | never sent by Cognito (ADR-0028 consequences) | ADR-0028, not built | none |
| Rotation grace | provider's own | 60 s in the cell (`main.tf:128`) | provider's own (Ping: "Refresh Token Rolling Grace Period") | 10 s default (`TestIdpFaults.cs:26`) |
| Silent hop (`prompt=none`) for fragments | yes | yes, managed login (`IdpProfiles.cs:52-57`) | not decided here | supported |

## Ping's documentation

Read on 2026-11-22: "Configuring OAuth clients", PingFederate 13.1,
https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html.
The page was read through a fetch tool that summarises it, so the setting names below are taken
from that summary and are not verbatim quotes. They map the requirements above onto the client
form. This records documentation only, not product behaviour.

- Client authentication offers client secret, client secret JWT, private key JWT and client TLS
  certificate, with a JWKS URL or JWKS for the key. There is a secret retention period for previous
  secrets, which is the two-secret rotation's counterpart.
- Redirect URIs, with wildcards allowed. Whether a port is accepted is not on the page read.
- Grant types are selected per client. A PKCE requirement is not on the page read.
- "Require Pushed Authorization Requests" is a per-client setting, which needs PAR enabled on the
  authorisation server.
- The refresh token settings are: a rolling policy, a rolling interval, a rolling grace period, and
  "Require Scope Offline_Access to Issue Refresh Tokens".
- "Persistent Grants Max Lifetime" and "Persistent Grants Idle Timeout".
- Default access token manager, which decides JWT or reference tokens and the audience (P2-6 decision
  item 8).
- Front-channel logout URIs, back-channel logout URIs and post-logout redirect URIs, shown only when
  the authorisation server tracks user sessions for logout.
- ID token lifetime is not on the page read.

P2-6 decision item 8 leaves four facts for a deployed run, and they stay open here:
- whether `aud` survives a refresh that names no `resource`;
- whether the token endpoint accepts `resource`;
- RP-initiated logout against a real `end_session_endpoint`;
- whether the provider accepts the gateway's assertion.

## Executed and read

**Executed:** nothing that exercises the gateway or a provider. No build, no test, no Terraform
plan or apply, no AWS call, no PingFederate. The commands run were `git`, `grep`, `sed`, `awk` and
one `python3` read of `registry.schema.json`, all against the worktree at `4035fe95`, plus `gh issue
view` on this ticket, its map and #332.

**Read**, all on 2026-11-22:
- **This repository at `4035fe95`:** every file named under Method.
- **The tracker:** #1477, #1478, and the D1 decision comment on #332.
- **Ping's documentation:** the one PingFederate 13.1 page above, through a summarising fetch, and
  a search of docs.pingidentity.com that found it.
- **The OpenID Connect RP-Initiated Logout 1.0 point** in "Lifetimes" item 4 is cited from the
  specification as this repository's profiles already cite it (`IdpProfiles.cs:15`). It was not
  re-read today.
