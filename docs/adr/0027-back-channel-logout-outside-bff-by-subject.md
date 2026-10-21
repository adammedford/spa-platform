# ADR-0027: Back-channel logout outside `/bff`, ending a subject's sessions through a session index that lands with its endpoint

- **Status:** Accepted
- **Date:** 2026-10-21
- **Deciders:** integrator, on Adam's answers to
  https://github.com/adammedford/spa-platform/issues/224 (wayfinder map #215)
- **Plan ref:** `docs/plan/06-phase-2-and-risks.md` → P2-6, at G7; change request
  `contracts/changes/p2-6-pingfederate-profile.md` item 7. Copies the placement of ADR-0024's
  refresh lease, not its timing.

## Context

OpenID Connect Back-Channel Logout 1.0 lets a provider tell a relying party, server to server and
with no browser present, that a user's sign-in has ended. The provider `POST`s a signed logout token
to a URI the client registered (section 2.5), and the relying party ends the matching sessions and
answers 200 or 400 (section 2.8). PingFederate supports it, and P2-6 item 7 asked for the shape to be
decided now and built later, so that the durable session store and the refresh lease leave room for
it.

Three facts shape the decision. The request gate holds `/bff` to same-origin browser requests
(ADR-0007), and a provider's request carries no fetch metadata and no cookie. The P2-1 table keys a
session by the SHA-256 of its key and never holds the key itself
(`p2-1-dynamodb-session-store.md:201`), while `SessionEnd` and the session gate both take the key.
And `SessionRecord`, frozen at G1b, carries `AppId` and `Subject` and no provider session id.

## Decision

1. **The endpoint is `/signout-backchannel-oidc`, outside `/bff`**, on each app's own host and
   mapped by `MapPlatformBff` for every app and every profile. Its gate row is `POST`,
   `Sec-Fetch-Site` Any, nothing more at the gate. Each provider client registers the URI on its
   app's host, so the token's `aud` is checked against that app's client id.
2. **The endpoint validates the spec's mandatory steps** (section 2.6) through
   `JsonWebTokenHandler` against the scheme's discovery configuration, and checks four claims
   itself: `events`, no `nonce`, `sub`, and `typ` absent or `logout+jwt`. A refused token is a 400
   rejection under a new rule id, `auth.backchannel`.
3. **Sessions end by subject**, option (b) of the three weighed. The session index is `ISessionIndex.RemoveSubjectAsync(appId,
   subject, ct)`: it removes every live session of one subject on one app, never another app's, and
   returns the records it removed, from which the endpoint revokes refresh tokens. It hands out no
   session key, so the table's "never holds the key" stays true.
4. **The index lands with its endpoint at G7**, in one landing with the rule id, the gate row, the
   reserved path and the stage prose. It is a new type beside `ISessionStore`, frozen on landing,
   implemented by the session store's own instance, with `InMemorySessionStore` as its double and a
   contract suite that takes both interfaces. It does not land between gates before P2-1's store.
5. **A `jti` is remembered per task until its `exp`.** A repeat on the same task is refused. A
   replay that reaches another task is accepted.
6. **A token with `sid` and no `sub` is refused.** With both, every session of that `sub` on the app
   ends.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| The endpoint under `/bff` | The request gate refuses a request with no fetch metadata on `/bff` (`gate.fetch-site`). Admitting this one would put an exception into the rule the gate holds for every `/bff` path, and the endpoint would sit among the paths `contracts/http-contract.md` documents for SPA authors. |
| (a) `KeysAsync(appId, subject)` returning session keys, with P2-1's item holding the key under data protection | Nothing downstream changes, but "the table never holds the key" stops being true, and plan row P2-1's wording with it. The exposure is no worse than the ticket's, which already holds the refresh token under the same envelope; it is a reversal of a stated choice for no gain (b) lacks. |
| (c) An opaque handle type with find, get and remove by handle | Three members and a second key vocabulary, to buy what (b) buys with one. |
| Landing the index between gates, before P2-1's store | It would make a frozen, one-way contract part of the identity critical path at G6 for a consumer that arrives at G7. Waiting costs little: P2-1's item already carries `appId` and `subject`, and DynamoDB adds a global secondary index to a live table and backfills it while the table stays available, so no item is rewritten later. |
| A shared `jti` memory across tasks | One more store contract for a check the spec makes optional (section 2.6 step 8). A replay within the token's short lifetime only ends the same subject's sessions again. |
| Ending one session by `sid` | The record has no session id, and the handler's `sid` sits only inside encrypted ticket bytes no index can read. Adding `sid` to the record waits for a gate. |

## Consequences

- The build waits for G7: a new rule id waits for a gate, the reserved path narrows what an SPA may
  serve, and the gate row edits `contracts/request-gate.md`, frozen at G1a.
- P2-1's store does not implement the index at G6. At G7 it does, and workstream 1 adds the index on
  `appId` and `subject` to the table.
- Index queries are eventually consistent, so a session created in the same second as a logout may
  survive it. The index's contract says so rather than promising more than DynamoDB gives.
- A replayed logout token that reaches a second task ends that subject's sessions on the app again,
  including a session created since the first delivery. This is accepted.
- A provider that sends `sid` alone gets 400s and ends nothing. Clients register
  `backchannel_logout_session_required` as false, and whether PingFederate then sends `sub` is a fact
  for the deployed run, with whether it reaches the app host on the internal ALB.
- The endpoint is profile-blind. A provider that never sends a logout token, Cognito among them,
  never reaches it.
- `GLOSSARY.md` gains *Back-channel logout* and *Session index*.

## Evidence

Change request `contracts/changes/p2-6-pingfederate-profile.md`, item 7 and its Decision; the
grilling round and answer on https://github.com/adammedford/spa-platform/issues/224; OpenID Connect
Back-Channel Logout 1.0, Final incorporating errata set 1 (2023-12-15), sections 2.2 and 2.4 to 2.8
and 4; "Adding a Global Secondary Index to an existing table",
https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.OnlineOps.html; the P2-1 item
layout, `contracts/changes/p2-1-dynamodb-session-store.md` section 6.
