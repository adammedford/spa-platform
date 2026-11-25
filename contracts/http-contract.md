# Browser contract

- **Freezes at:** G1a. The cookie table and the `/bff` endpoints describe identity behaviour, so
  those parts freeze at G1b, recorded by the annotated `contracts-v1` tag (ADR-0017).
- **Read by:** an SPA author, and the gateway
- **Source:** ADR-0007, ADR-0008

What an SPA author needs to know. The repository's browser client is a reference implementation of
it, which an SPA may copy and change; this contract is the promise.

## Endpoints

| Request | Made as | Behaviour |
| --- | --- | --- |
| `GET /bff/session` | Fetch | 200 with `sub`, `name`, `email`, `expiresAt`, `csrfToken` and `fragments`; `name` and `email` may be null. `expiresAt` is `SessionRecord.ExpiresAt`, the session expiry, not the access-token expiry. `fragments` maps every `fragmentId` of the app's current entry to `signedIn`, `unavailable` or `loginRequired` (Fragments → "Sign-in"), and is `{}` for an app with no fragments. 401 when there is no session. Never contains a token. |
| `GET /bff/fragments` | Fetch | 200 with `{"fragments": [{"fragmentId": …, "entryUrl": …}]}`, one item for every fragment of the app, and `Cache-Control: no-store`. `entryUrl` is `/fragments/{fragmentId}/{entry}` and names no release: the path resolves to the live one. 401 `unauthenticated` when there is no session. |
| `GET /bff/login?returnUrl=/path` | Navigation | Discards any existing session, then redirects to the provider. `returnUrl` must be a local path, or it becomes `/`. For an app with fragments, the login callback then signs in to each fragment's client in turn through `/bff/chain` before redirecting to `returnUrl` (Fragments → "Sign-in"). |
| `GET /bff/login?fragment={fragmentId}&returnUrl=/path` | Navigation | Signs in to that one fragment's client and redirects to `returnUrl`; discards nothing. Started only by the user (Fragments → "Sign-in"). |
| `POST /bff/logout` | Form navigation | Attempts to revoke every token set's refresh token, the shell's and each fragment's, always removes the session even if a revocation fails, then redirects once through the provider's logout back to `/` (Fragments → "Logout"). |
| Any method on `/api/{name}/{rest}` | Fetch | Proxied to the app's upstream called `name`, with `/api/{name}` removed. 404 for a name the app does not list. |
| Any method on `/fragments/{fragmentId}/api/{name}/{rest}` | Fetch | Proxied to the upstream called `name` in that fragment's `apis`, with `/fragments/{fragmentId}/api/{name}` removed, carrying the fragment's access token and never the shell's. 404 for a name the fragment does not list. 401 `fragment_login_required` when the session holds no usable token set for the fragment. |
| `GET` or `HEAD` on `/fragments/{fragmentId}/{rest}` | Subresource | The asset at `{rest}` in the fragment's live release. No `index.html` fallback. |
| `GET` or `HEAD` on anything else | Navigation or subresource | The asset at that path. For a path with no extension that accepts HTML, `index.html`. |

Two kinds of path are reserved. A path reserved outright is served from no release, the shell's
or a fragment's: `/bff/`, `/api/`, `/fragments/{fragmentId}/api/`, `/signin-oidc` (and
`/signin-oidc/{fragmentId}`), `/signout-callback-oidc`, `/signout-oidc`, `/healthz` and `/readyz`.
A path reserved from the shell's release is served by the platform and never from the shell's
release: `/fragments/`, on every host. On a host whose app has no fragments, a path under
`/fragments/` answers 404 under rule id `path.reserved`; a `fragmentId` the app does not list
answers 404 `not_found`. `spactl fragment publish` refuses a release with a top-level `api/`.

Every 404 the two asset rows answer carries `Cache-Control: private, no-store`: a key the store
answered as absent, a key whose read the store refused, and a reserved path refused before any
read. Under the deployed role every missing asset is a refused read, and RFC 9110 section 15.5.5
lets a cache keep a 404 heuristically, so without it a 404 caused by a passing permission fault
could outlive the fault. The asset route's `shell_unavailable` 500 carries it too. The app's
document is the key whose release-relative path is exactly `index.html`, compared ordinally: `/`,
`/index.html` and every extensionless fallback, including one under `/assets/`, resolve to it, and
`/Index.html` and `/assets/index.html` do not (change request
`contracts/changes/issue-381-object-read-refusal.md`, items 3 and 4). A fragment's release has no
`index.html` fallback: under `/fragments/{fragmentId}/` every path, with an extension or without,
is the key at that path or a 404.

The `/bff` endpoints, `/bff/chain` among them, and the session record share the G1b freeze
(ADR-0017). The annotated `contracts-v1` tag is the freeze record for the three endpoints it froze;
the endpoints added for fragments joined the freeze by change request #982's decision. Identity
implementation stays blocked until the tag exists.

## Fragments

An app may be a shell: its registry entry lists fragments, each one owner's unit with its own
OAuth client, release, APIs and content security policy additions, mounted into the shell's
document at the shell's host. The shell owns the document, the baseline policy, the session and
the composition. An app with no fragments behaves as the rest of this contract describes.

The boundary between fragments is logical, not isolation. Every fragment runs in the shell's one
document at one origin, where scripts cannot be told apart, so one fragment's code can call
another's namespace. What the gateway keeps apart is the token: a fragment's route forwards that
fragment's token, never the shell's or another fragment's.

### Discovery and loading

The shell discovers its fragments with `GET /bff/fragments` and loads each with
`import(entryUrl)`; that is the only discovery contract. The entry module is an ES module at a
name constant across releases at the root of the fragment's release, `entry.js` unless the entry
names another, so `entryUrl` names no release. The baseline `script-src 'self'` already admits
every same-origin module load. Shared dependencies, if any, use one static import map in the
shell's `index.html`, allowed by a `'sha256-'`, `'sha384-'` or `'sha512-'` source in the shell
entry's `script-src`. A fragment pins no shared version, a registry write is refused when the
shell's document carries a second map or a map whose hash the policy lacks, and the gateway never
transforms the document.

### Sign-in

One sign-in covers every fragment. When the shell's login callback completes, the gateway chains
one silent authorization code flow per fragment, a hop, in registry order, through `/bff/chain`
(below), merging each fragment's token set into the one session, then redirects to `returnUrl`. A
hop never prompts. A hop that fails marks that fragment and the chain continues, so the shell
still loads. Each fragment's callback is `GET /signin-oidc/{fragmentId}`, gateway-internal as
`/signin-oidc` is.

`GET /bff/session` → `fragments` reports every fragment of the current entry. `signedIn`: the
session holds a token set for it, issued to its current client. `unavailable`: a hop or a
fragment login met a definitive refusal (`invalid_client`, `unauthorized_client`,
`invalid_scope`, `invalid_grant` on the code, or a token set issued for another `sub` than the
session's) and the mark's write was confirmed. `loginRequired`: anything else, including a
fragment onboarded after this login, a hop that never returned, a provider that answered
`login_required` or failed transiently, and a store failure, which never produces `unavailable`.
The shell mounts a `signedIn` fragment; for `loginRequired` or `unavailable` it renders a control
instead of mounting it.

The control navigates to `GET /bff/login?fragment={fragmentId}&returnUrl={here}` on the user's
gesture. The shell never starts that login by itself, and nothing escalates `login_required`
automatically. It sends no `prompt=none`: inside the provider's session it completes without a
page, and after it the provider asks, because the user asked. It discards nothing; it replaces any
token set the fragment had without revoking the old one; with no session it is the ordinary login,
chain included; for a `fragmentId` the app does not list it redirects to `returnUrl` and changes
nothing; on a failed sign-in it writes `unavailable` only for a definitive refusal whose write is
confirmed, otherwise nothing, and redirects to `returnUrl`; on a session store failure it answers
503 `session_store_unavailable`.

### Token sets

The session holds one token set per client, the shell's and one for each fragment, each with its
own expiry and refresh token. `expiresAt` is the shell's and the session's only expiry. A
fragment's refresh refused with `invalid_grant` ends only that fragment's set, and the call
answers 401 `fragment_login_required`; the shell's ends the session, 401 `session_ended`. A
fragment API call with no usable token set for its fragment answers 401
`fragment_login_required`; with no session at all, 401 `unauthenticated`. A token set recorded to
a client the entry no longer names reads as absent.

The session record's shape does not preclude acquiring a fragment's token set server-side by
token exchange (RFC 8693), a route S9 may take; the record carries no field for it.

### `/bff/chain`

`/bff/chain` is the step between two hops of the sign-in chain: a navigation the gateway redirects
to itself after the provider's redirect. It is gateway-internal, not for SPA authors: an SPA never
links to it, calls it or reads it.

- **The request.** `GET /bff/chain?c={continuation}` only; any other method answers 405 under
  rule id `gate.method`. `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` are required,
  or 403 `origin_rejected` under `gate.navigation`. `Sec-Fetch-Site` may be any value: Fetch
  Metadata computes it over the navigation's whole redirect chain, so after the provider's origin
  it is `cross-site`, or `same-site` under a custom domain sharing the site, at every later hop.
  No antiforgery token: a navigation cannot carry one. The step's protection is its continuation,
  as a callback's is its state.
- **The continuation** `c` is minted by the shell's login callback and by each fragment hop, and
  protected by the data-protection key ring under a purpose naming the app and `chain`. It
  carries the session key it was minted for, the snapshot of the app's `fragmentId`s in registry
  order, the position of the next fragment, the validated local `returnUrl` from `/bff/login`,
  and an expiry, `ChainLifetime`, five minutes from minting, renewed by each hop's new
  continuation. Nothing in the query is read but `c`, so a caller can choose neither the position
  nor the `returnUrl`.
- **Binding.** The step requires the session: a cookie that resolves to no session for this app
  gets the usual challenge, and the session's key must equal the continuation's. A continuation
  minted for another session or another app, or past its expiry, is refused.
- **Position.** At each step the gateway skips every `fragmentId` of the snapshot the current
  entry no longer lists, and every one whose token set the session already holds as `signedIn`
  with its current client, so a replayed step does no work twice. A position past the snapshot's
  end, or nothing left to do, ends the chain with a redirect to the continuation's `returnUrl`. A
  fragment added mid-chain is not chained; it reads `loginRequired`.
- **Refusal.** A missing, unreadable, expired or mismatched continuation answers 302 to `/`, the
  app's root and never a caller-supplied URL, with no problem body and no change to the session,
  under rule id `auth.chain`. It discards nothing: the session stands.
- **Store failure.** A session store operation that fails inside a step answers 503
  `session_store_unavailable`, as on a login callback.

### Logout

`POST /bff/logout` enters the shell's session gate, then each fragment's in ordinal `fragmentId`
order, starting holding none, and takes no refresh lease. Its waits for those gates share one
`LogoutGateBudget`, equal to the refresh write deadline (20 s, the longest a holder keeps a gate),
measured from logout's entry; a gate not entered within it is skipped with the outcome
`gate_timeout`, and logout proceeds. It then re-reads the session and revokes every token set in
parallel under one `ProviderDeadline`, each through its own client in the profile's credential
style; a set with no live scheme, or recorded to a client the entry no longer names, is not
revoked (outcomes `no_scheme` and `client_changed`). One log line records each set's outcome.
Then it removes the session, and redirects once through the provider's logout by the shell's
client with its ID token. A token no current credential can revoke, such as a removed fragment's
or a rotated client's, is dropped unrevoked and logged, and lives at the provider until it expires.

The bound. The clock starts at logout's entry. The whole operation takes at most
`LogoutGateBudget` + `StoreDeadline` (the re-read) + `ProviderDeadline` (the parallel
revocations) + `StoreDeadline` (the remove, under the `continuity` spec's F11) = 20 + 5 +
10 + 5 = 40 s at today's values, inside the load balancer's idle timeout; revocation and removal alone, from the
re-read's return, take at most `ProviderDeadline` + `StoreDeadline`. The provider logout is the
browser's redirect and outside the bound.

### Fragment activation

A fragment's release is activated by one registry change, with no gateway restart, and the
shell's release does not change. Activation is request-time: it applies to requests that follow
once a gateway task has read the registry revision, and each task reads it independently, so
while tasks disagree one document's requests can reach either release. Modules already loaded
stay in the document; a fragment first imported after activation loads the new release; a later
chunk missing from both the live release and the browser cache fails with 404. There is no
one-release-per-document guarantee. Reload is the recovery.

## Cookies

The cookie names and attributes belong to G1a; the session record behind `__Host-spa-session`
belongs to G1b (ADR-0017).

| Cookie | Attributes | Holds |
| --- | --- | --- |
| `__Host-spa-session` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain` | A session id, encrypted for this app's scheme |
| `__Host-spa-csrf` | `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain` | The antiforgery cookie token. Cleared at logout. |
| `__Host-spa-corr.*` and `__Host-spa-nonce.*` | `Secure; HttpOnly; SameSite=Lax; Path=/`, no `Domain`, minutes (`SameSite=None` under a profile that uses `form_post`: `pingfederate`, change request P2-6) | Login state. Deleted by the callback. |

Every cookie carries the `__Host-` prefix, so only the app's own host can set it. That includes the
login-state cookies: under their framework names a sibling SPA could plant them and complete a
login as another user.

Two cookies persist in the browser, the session cookie and the antiforgery cookie, and neither holds
token material. Correlation and nonce cookies exist only while a login is in flight.

## Request headers the SPA sends

| Header | On | Value |
| --- | --- | --- |
| `X-CSRF-TOKEN` | Every unsafe method on `/api/**` and `/fragments/{fragmentId}/api/**`: anything but `GET`, `HEAD` and `OPTIONS` | The request token, read from `csrfToken` in the `GET /bff/session` response; one token for the app, its fragments' APIs included |

The token travels in this header and nowhere else. It is never a form field, because the gateway
would then have to read a request body it is about to proxy, so antiforgery runs with form-body
reading suppressed (`contracts/request-gate.md`, rule `csrf.token`). A request missing it, or
carrying another app's, is refused with 403 and `csrf_failed`.

## Response headers

Set on every response, including proxied ones, where they overwrite what the upstream sent.

- `Content-Security-Policy`: the baseline below plus the entry's additions and each fragment's
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Resource-Policy: same-origin`
- `Strict-Transport-Security: max-age=31536000`
- No `Access-Control-*` header, ever. One sent by an upstream is stripped.

### The baseline content security policy

```text
default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self';
connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none';
form-action 'self' <identity provider origin>
```

The provider origin is `Issuers:{name}:BrowserOrigin`. It is in `form-action` because logout is a
form post that ends in a redirect to the provider; some browsers apply `form-action` to that
redirect, and Chromium has done so. The deployed logout test settles it for the managed browser.

An entry's `csp` adds sources to these ten directives and to no others, which is why
`registry.schema.json` lists exactly them. `*` and `'unsafe-eval'` are rejected in every directive;
`'unsafe-inline'` and `data:` are rejected in `script-src`. `data:` elsewhere is allowed, and the
example entry uses it in `img-src`.

A fragment's `csp` adds sources to `script-src`, `style-src`, `img-src`, `font-src` and
`connect-src` only, under the same rejections, and its `script-src` carries no `'strict-dynamic'`,
nonce or hash. A host sends one policy on every response: each directive is the baseline's
sources, then the shell's, then each fragment's in registry order, without duplicates. So a
fragment's addition applies to the whole document.

## Errors

Errors are `application/problem+json` with a stable `code`. `/api`, `/fragments/{fragmentId}/api`,
`/bff/session` and `/bff/fragments` never answer with a redirect. Kestrel refuses oversized headers
itself, with 431 and no body.

| Status | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | The path failed normalisation, or a cookie name imitates a platform cookie |
| 401 | `unauthenticated` | No session |
| 401 | `session_ended` | The refresh token was refused and the session was removed |
| 401 | `fragment_login_required` | A fragment API call found no usable token set for that fragment in a live session; the session is kept, and the body names no fragment |
| 403 | `origin_rejected` | The request gate refused the request |
| 403 | `csrf_failed` | The antiforgery token was missing or wrong |
| 404 | `not_found` | Unknown host, unknown `fragmentId`, missing asset (any key but the app's document), or a reserved path that is switched off |
| 404 | `unknown_api` | The app, or the fragment the path names, lists no upstream under that name |
| 405 | `method_not_allowed` | The path does not accept the method |
| 500 | `shell_unavailable` | The app's release has no readable index.html |
| 502 | `upstream_unavailable` | The upstream could not be reached |
| 503 | `idp_unavailable` | The identity provider could not be reached to refresh the token or failed the refresh other than with `invalid_grant`, no refresh was started because the gateway is stopping, or another writer replaced the session during the refresh and left it with no access token; the session is kept |
| 503 | `store_unavailable` | The object store could not be read to serve an asset or the app's document, for a reason other than a refusal; the live snapshot is unaffected |
| 503 | `session_store_unavailable` | A session store operation failed or ran out `StoreDeadline`, or a refresh ended at the store's bound (2 × TTL, the `continuity` spec's D2); not the session remove `POST /bff/logout` makes, which answers the usual logout (F11); no cookie is cleared and the session, if it exists, is kept |
| 504 | `upstream_timeout` | The upstream exceeded the entry's timeout |

Two refusals are not a problem body. A login callback the OpenID Connect handler refuses (a bad
state, correlation or nonce, or a provider error) is answered with the one static failure page:
HTML, 400, `Cache-Control: no-store`, nothing reflected from the request, rule id `auth.callback`.
It is a page because the browser arrived by navigation and a person reads it (N-05, N-18). At a fragment's
callback, `/signin-oidc/{fragmentId}`, only a foreign or unreadable state gets that page, and the
session is unchanged; a provider error or a refused code under readable state marks the fragment
as Fragments → "Sign-in" says, and the chain continues. A refused `/bff/chain` step answers 302 to
`/` under rule id `auth.chain` (Fragments → "`/bff/chain`").

The `code` says little on purpose. Which rule fired is in the log line and in
`security.rejections`, under the rule ids in `contracts/request-gate.md`. Both the method
allowlist and the request gate write `gate.method` for their 405 rejections.

## When a dependency is unavailable

What the gateway answers, logs and keeps when a dependency it reads cannot be read, so a supporter
reads one table. "Snapshot" is the live registry snapshot (`GLOSSARY.md` → Last known good). A row
is here once a landed test proves its behaviour (change request #543). Cases whose answer is
contracted elsewhere are not repeated: the codes in Errors above, `idp_unavailable` among them; an
upstream that cannot be reached, stays silent past its timeout or answers `5xx`, in
`contracts/upstream-contract.md`; and a registry document that cannot be read, is missing or is
invalid on a poll, which leaves the live snapshot serving (ADR-0002). A case named in none of these
has no answer the contract promises yet.

| Dependency, case | Answer | Logged | Snapshot |
| --- | --- | --- | --- |
| The object store refuses a read for an asset (S3 `AccessDenied`, how every missing key arrives under the deployed role) | `404` `not_found` with `Cache-Control: private, no-store`, no rule id; for the app's document, `500` `shell_unavailable` with the same header | The request line's `read_refused`, `asset` or `shell` (`contracts/telemetry.md`), never the key; for the app's document, one Error naming the app, the release and that the store refused | Stays |
| Parameter Store cannot be read at a cold start, for the secret of every app in the document (when only some cannot, those apps are left out and the rest go live: `contracts/README.md`, `clientSecretRef`) | `/readyz` stays `503`; every app path is `404` `not_found`, since before a snapshot every host is unknown | One Warning per app whose secret cannot be read, naming the app and the reference, never the value; one Error per poll that the revision is not applied | There is none, and each poll reads the document again |
| The in-memory session store is at `Session:MaxSessions` | The login succeeds; the session nearest its expiry, an expired one first, is ended, and that session's next request is `401` `unauthenticated` | Nothing at the eviction | Stays |
