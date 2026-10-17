# 01: I-6 — Close G1b: fold G0 findings in and tag contracts-v1
**Task:** I-6
**Owner:** integrator
**Blocked by:** 3.0, 47
**Status:** resolved

**What to build:** Close G1b: fold the G0 findings into the identity contracts, record them as the first decision record, and tag the freeze. Needs G1a (tagged `skeleton-v1`) and G0. Contracts it edits: the draft parts listed in `contracts/README.md` -> "What is draft until G1b" (`registry.schema.json#/$defs/oauth`, `app-definition.schema.json#/properties/oauth`, the `Profile` enum in `platform-config.schema.json`, the `/bff` endpoints and session record in `contracts/http-contract.md`, and the identity interfaces in `src/Platform.Abstractions`). Gate: G1b in `docs/plan/01-plan.md`. Done when: tag `contracts-v1` exists.

## Acceptance
- [ ] G0 has reported: the five gate items from task 3.0 pass, or the plan's way out is taken
- [ ] G0 findings folded into the draft identity contracts and `src/Platform.Abstractions` identity interfaces
- [ ] Findings recorded as a decision record in `docs/adr/`
- [ ] Annotated tag `contracts-v1` exists (G1b)
- [ ] Ticket 47 has landed first. I-6 changes the bytes of three schema files; while
      `src/Platform.Registry/Schemas/` holds copies, `SchemaCopyTests` fails until each copy is
      refreshed, and `.github/path-ownership.py` refuses a push whose diff spans the integrator's
      paths and workstream 2's. So I-6 cannot land green in one push until 47 is in. The
      integrator's half, `COPY contracts/*.schema.json contracts/` in `src/Gateway/Dockerfile`,
      landed with the decision on change request 2.1 on 2026-10-14

## Why this status
This is the integrator's gate decision and the head of the chain that holds 3.1 to 3.9 and 4.2. It cannot be done by an agent: it needs G0's report, which needs the Cognito half of task 3.0 run against a real user pool and app clients, and then a human integrator decides what the findings change and tags the freeze. Listed as ready-for-human so the morning sees it as the next human step once 3.0 reports; blockedBy names 3.0.

## Grilling record

Run on paper overnight (13 October 2026). Every recommendation below was adopted provisionally to
open the next round; none is decided until Adam answers "Questions for Adam".

### Facts established from the repo (not questions)

- **G1a is closed.** `git tag` shows `skeleton-v1` ("G1a: ... Proved by <CI run URL>"). That message
  shape is the convention for gate tags.
- **G0 has not reported, and the chain is longer than "Blocked by: 3.0" says.** Neither task 1.1 nor
  1.8 has landed (`git log`), 3.0 is blocked by 1.8, and `spikes/g0/` has no Cognito harness yet.
  Items 1, 4 and 5 passed on 11 October 2026; items 2 and 3 and the five sub-questions remain.
- **"Record them as the first decision record" is stale.** The plan (frozen export) predates
  `docs/adr/`, which now holds ADR-0001 to ADR-0013. The G1b record would be ADR-0014 at the
  earliest. Also stale for the same reason: `docs/agents/domain.md` and `CLAUDE.md` say
  "ADR-0001 to ADR-0012".
- **The draft set in the repo is wider than the ticket's "Contracts it edits".** Everything I-6 has
  to touch to end "draft":
  - `contracts/registry.schema.json` `#/$defs/oauth` (`x-freeze` status `draft`) and the root
    `x-freeze.exceptions`; the same pair in `app-definition.schema.json`; the `Profile` enum's
    `x-freeze` in `platform-config.schema.json`.
  - `contracts/http-contract.md`: two "draft until G1b" paragraphs (endpoints, cookies). Markdown,
    so no machine marker.
  - `contracts/README.md` -> "What is draft until G1b".
  - `src/Platform.Abstractions`: `[Freeze("G1b")]` on `IAccessTokenSource`, `IIdpProfile`,
    `OAuthDefinition`, `SchemeNames`, `SessionRecord`; the `FreezeAttribute` doc comment.
  - Tests that assert the draft state: `tests/Contracts.Tests/ContractIndexTests.cs` (two facts
    assert `status: draft`), `tests/Platform.Abstractions.Tests/FreezeMarkersTests.cs` (`CodeForm`
    must equal the draft subschemas, so it fails the moment a draft marker goes unless it shrinks
    with it), `PublicSurfaceTests.DraftUntilG1b` (its comment already says I-6 "removes these
    markers").
  - Prose that names the tag: `AGENTS.md:90`, `src/Platform.Identity/PlatformIdentityExtensions.cs:13`.
    The latter is workstream 3's path, so I-6 leaves it to task 3.1.
- **Two code contradictions.** `AccessTokenResult` says "Draft until G1b" in its doc comment but
  carries no marker, and `ISessionStore` says "Freezes with SessionRecord at G1b" but carries no
  marker. `FreezeAttribute` says "an unmarked type freezes at G1a", so by the code's own rule both
  froze at G1a and changing either now needs a change request. The plan (02-contracts line 20)
  names "the session record, `IAccessTokenSource`" without saying whether the result type and the
  store interface ride along.
- **Vocabulary collision.** The Evidence tab uses "finding" for a probe-confirmed behaviour with an
  id (R-C7, T-B14: "23 probes confirmed a finding"). The ticket uses "G0 findings" for the gate's
  verdicts and answers. Same word, two things.
- **What each item-2 sub-question can move** (from `docs/plan/07-evidence.md` and the ADRs):
  grace-window replay -> ADR-0005 reserve design (T-B6, T-B8, T-D10); revoke-successor -> `/bff/logout`
  (T-B18); refresh-token length over 4,096 -> ADR-0005, item 4's way out; `aud` after a refresh
  without `resource` -> `OAuthDefinition.Resource` and `upstream-contract.md` line 15; revocation
  endpoint in discovery -> `IIdpProfile` (T-B14: the library's revoke throws when discovery lists
  none). Item 3 touches no draft part: hosts with ports live in fixtures (`app1.spa-poc.test:8443`)
  and in `platform-config.example.json` (`.spa-poc.test:8443`).
- **Scenario check on `/bff/session`.** app1 and app2 ask for `openid` and `profile` and not `email`,
  yet the contract promises `email` in the body. Whether Cognito puts `email` in the ID token then
  depends on the client's readable attributes, not on the contract.

### Round 1

❓ **Q1 - Where does "G0 has reported" land?** Task 3.0 says "report the five gate items to the
integrator" and names no place. Options: (a) a section in `spikes/g0/README.md` plus a recorded
output file beside the Cognito harness, as items 1, 4 and 5 did; (b) a comment on this ticket;
(c) straight into the ADR.

➡️ (a). One line per gate item and per sub-question, with the run's date and pool. The ADR cites it.
It follows the existing pattern, and the evidence then lives with the harness that produced it.

---

❓ **Q2 - Does G1b need all five gate items, or only item 2?** Item 3 (a callback URL with a port,
managed login on a Terraform-made client) changes no draft part. Options: (a) all five, as the plan's
gates table says; (b) item 2 only, with item 3 recorded as open and owed before G3.

➡️ (a). The plan defines G0 as five items and G1b as "G0's findings folded in", and item 3's way out
(drop the port) changes the `Host` values that every fixture and the platform-config example carry.
Keeping the gate whole avoids a freeze that is followed at once by a host change.

---

❓ **Q3 - What blocks the tag?** Item 2's five sub-questions are questions, not pass/fail checks.
Options: (a) any surprising answer blocks; (b) only a failure of item 2's core (two clients complete
login, callback, refresh, logout; `aud` set by resource binding; rotation returns a new refresh
token) blocks, and each sub-question has a response agreed in advance (round 2); (c) nothing blocks.

➡️ (b). That way a surprising answer needs no new meeting: it triggers the response agreed in round 2.
Only a core failure with no fix inside the task stops identity work, as the plan already says.

---

❓ **Q4 - Are `AccessTokenResult` and `ISessionStore` in the G1b draft set?** Their doc comments say
yes and their markers say no (see facts).

➡️ Yes. `AccessTokenResult` is the return type of `IAccessTokenSource` and `ISessionStore` is the only
way to reach a `SessionRecord`, so neither can be frozen without the other. I-6 may change them
without a change request, and the README table names them.

---

❓ **Q5 - What shape is the decision record?** The plan requires one. Domain-modelling's rule says an
ADR needs a hard-to-reverse trade-off, and a clean pass on all five items is evidence, not a
trade-off. Options: (a) always a new ADR-0014, "Freeze the identity contracts on the G0 report",
plus status lines on ADR-0001, 0005 and 0011; (b) status lines only, plus ADR-0014 only when a
round-2 branch forces a choice.

➡️ (a). The freeze itself passes all three tests. It is hard to reverse, because `SchemeNames` sets
data-protection purposes and a rename invalidates every issued cookie. It is surprising without
context, because a reader will ask why identity froze a day after everything else. It is a real
trade-off between freezing now and leaving identity draft through 3.x. The record also gives
round 2's conditional choices one place to live.

---

❓ **Q6 - Does the `Profile` enum keep `standard` as well as `cognito`?** Phase 1 configures only
`cognito`. `fixtures/TestIdp` serves both logout styles, and the hermetic tier runs on it.

➡️ Keep both. The test identity provider exercises the standard style, and the PingFederate gate
needs it in Phase 2. Removing it would mean a change request later for a value that is used today.

---

❓ **Q7 - Should `GET /bff/session` freeze `name` and `email` as optional, and `expiresAt` as the
session's end?** No test app asks for `email`, and Cognito only puts a claim in the ID token when
the client can read it. `expiresAt` could mean the session's end or the access token's.

➡️ Yes on both. `name` and `email` may be null, `sub`, `expiresAt` and `csrfToken` are always
present, and `expiresAt` is `SessionRecord.ExpiresAt`. The access token's expiry is internal and
changes every five minutes.

---

❓ **Q8 - Should `POST /bff/logout` promise to remove the session whether or not revocation
succeeds?** It currently reads "Revokes the refresh token, removes the session". T-B14 already makes
task 3.9 remove the session even when the revoke throws.

➡️ Yes. Reword it to "attempts to revoke the session's refresh token, always removes the session".
The promise a browser can rely on is the removal, and no G0 answer changes that.

---

❓ **Q9 - Does the sentinel scheme stay out of `SchemeNames`?** Only `Platform.Identity` (task 3.8)
uses it, and workstream 4 never names it.

➡️ Yes, it stays private to `Platform.Identity`. Adding it to `SchemeNames` would freeze a name that
no other workstream consumes.

---

❓ **Q10 - Should the record say "G0 report" instead of "G0 findings"?** In the Evidence tab
"finding" means a probe-confirmed behaviour with an id.

➡️ Yes. Use "G0 report" for the gate's verdicts and answers and keep "finding" in its evidence
sense. The ADR, the tag message and this ticket's wording follow the new term.

### Round 2 (Q1 to Q10 adopted)

❓ **Q11 - What happens to the markers at the freeze?** Options: (a) mirror G1a: in the schemas,
`x-freeze.status` flips to `frozen-at-g1b` on the subschemas and the root `exceptions` arrays go;
in the code, every `[Freeze("G1b")]` goes, `FreezeAttribute` stays for Phase 2's gates, and its doc
says "marked = draft until its gate is tagged, unmarked = frozen"; (b) strip every marker and delete
`FreezeAttribute`; (c) keep `[Freeze("G1b")]` and redefine it to mean "the gate that froze it".

➡️ (a). The G1a shape that `ContractIndexTests` already encodes stays. The three freeze tests are
rewritten to assert an empty draft set, and `CodeForm` and `DraftUntilG1b` become empty. Phase 2
can mark a G6 draft without inventing a new mechanism.

---

❓ **Q12 - What does the tag record?** Options: (a) an annotated `contracts-v1` on the `main` merge
of I-6, with the message "G1b: the identity contracts are frozen on the G0 report (ADR-0014); the
draft set is empty. Proved by <CI run URL>", where that run is green on the commit; (b) a
lightweight tag.

➡️ (a). It is the `skeleton-v1` convention, and AGENTS.md tells readers to trust tags for gate state.

---

❓ **Q13 - If Cognito's grace window does not cover a replayed refresh token, does the reserve
design replace the library's locking in task 3.8 with `IAccessTokenSource` unchanged?**

➡️ Yes. ADR-0005 already names this trigger. The reserve design (T-D1 to T-D10) sits behind the same
interface, so no contract moves. ADR-0005's status line records the change and ADR-0014 cites T-D10's
caveat about deadlines.

---

❓ **Q14 - If a Cognito refresh token is longer than 4,096 characters, is item 4's way out taken: one
in-house class over Duende.IdentityModel behind `IAccessTokenSource`, with ADR-0005 superseded?**

➡️ Yes. That is the fallback the plan already names, and the interface hides it. This is the one
branch where ADR-0005 gets superseded rather than annotated, and the reason belongs in ADR-0014.

---

❓ **Q15 - If `aud` is lost on a refresh that sends no `resource`, does task 3.8 send `resource` on
every refresh, with `oauth.resource` frozen as it is?** If Cognito refuses `resource` on the refresh
grant altogether, the fallback is a second question: resource binding is declared unsupported for
the `cognito` profile in ADR-0014, upstreams rely on the `client_id` and scope checks already in
`upstream-contract.md`, and fixtures drop `resource`.

➡️ Yes to the first, and yes to the fallback if it is needed. The schema field stays either way,
because app2 already shows that "no resource" is a valid entry.

---

❓ **Q16 - If Cognito's discovery lists no revocation endpoint, does `IIdpProfile` gain an explicit
member that supplies one (for Cognito, on the issuer's `BrowserOrigin`), frozen at G1b?** The
alternative is to have `Configure` inject it into the OIDC configuration and hope the token library
reads it there, or to accept a logout that never revokes.

➡️ Yes, an explicit member. T-B14 shows the library throws without one. A member is testable against
the test identity provider's Cognito style, and adding it after the freeze would need a change
request. If discovery does list one, no member is added.

---

❓ **Q17 - If revoking a refresh token does not revoke its successor, does task 3.9 revoke the token
it reads after any in-flight refresh completes, with no contract change beyond Q8's wording?**
T-B18 is the race.

➡️ Yes. Q8 already makes the browser-facing promise "always removes the session". Revoking the right
token is 3.9's implementation, under the reserve design's lock if Q13 adopted it.

---

❓ **Q18 - If Cognito refuses a callback URL with a port, does I-6 take the way out, which is to
bind local port 443, drop `:8443` from `platform-config.example.json`, and open follow-up tickets
for fixtures (5.5) and the apps root (1.8) instead of editing their paths?**

➡️ Yes. I-6 owns the contract example but not `fixtures/` or `infra/`. The glossary's **Host** keeps
"with the port when one is used", because the deployed tier still needs that rule.

---

❓ **Q19 - If item 2 fails with no fix inside task 3.0, does I-6 stay open and untagged, with a new
integrator ticket comparing Finbuckle, Duende BFF v4 and a tenant-aware OIDC stack?**

➡️ Yes. The plan says identity work stops. Tagging a freeze on a mechanism that failed would let
3.1 to 3.9 start on it.

---

❓ **Q20 - Does I-6 also fix the stale prose: the ADR count in `docs/agents/domain.md`, `CLAUDE.md`
and `docs/adr/README.md` (including its "three decisions carry open gate work" paragraph), and
`AGENTS.md:90`?**

➡️ Yes. These are integrator paths and I-6 touches them anyway to add ADR-0014. Leaving them stale
would mislead the next session, which is the failure AGENTS.md warns about.

### Round 3 (Q1 to Q20 adopted)

Frontier empty. Checked for silent assumptions:
- Who runs I-6: the integrator (Adam). Dispatch after the tag (3.1 to 3.9, 4.2) is the existing
  table in `docs/plan/03-workstreams-and-tasks.md`, so it is a fact, not a decision.
- The `SessionRecord` fields: no G0 branch adds one. The reserve design uses the existing `Version`.
  Frozen as is.
- Scheme names `oidc:{appId}` and `cookie:{appId}`: Cognito never sees them. Frozen as is.
- Item 3's managed-login way out (a branding style made through the AWS CLI) is infra-only and moves
  no contract. ADR-0014 records it.
- `PlatformIdentityExtensions.cs` prose is workstream 3's path, so task 3.1 rewords it.

## Questions for Adam

1. Should task 3.0 report G0 as a section in `spikes/g0/README.md` plus a recorded output file, one
   line per gate item and sub-question? **Rec: yes.** Why: it follows the items 1, 4 and 5 pattern,
   so the evidence stays with its harness.
2. Does G1b wait for all five G0 items, item 3 included? **Rec: yes.** Why: item 3's way out changes
   every `Host` value, so freezing before it risks an immediate change.
3. Does only a failure of item 2's core block the tag, with each sub-question answered by a response
   agreed in advance (Q13 to Q17)? **Rec: yes.** Why: a surprising answer then triggers an agreed
   response instead of a new meeting.
4. Are `AccessTokenResult` and `ISessionStore` part of the G1b draft set, changeable by I-6 without a
   change request? **Rec: yes.** Why: one is `IAccessTokenSource`'s return type, the other the only
   path to `SessionRecord`, and their doc comments already say so.
5. Is the decision record always a new ADR-0014, "Freeze the identity contracts on the G0 report",
   plus status lines on ADR-0001, 0005 and 0011? **Rec: yes.** Why: the freeze is hard to reverse,
   because scheme names set data-protection purposes. ADR-0013 exists, so the plan's "first decision
   record" is stale.
6. Does the `Profile` enum keep both `standard` and `cognito`? **Rec: yes.** Why: the test identity
   provider runs the standard style today, and PingFederate needs it later.
7. Does `GET /bff/session` freeze `name` and `email` as nullable, and `expiresAt` as the session's
   end (`SessionRecord.ExpiresAt`)? **Rec: yes.** Why: no test app asks for `email`, and the access
   token's expiry is internal.
8. Does `POST /bff/logout` become "attempts to revoke the refresh token, always removes the session"?
   **Rec: yes.** Why: T-B14 already makes 3.9 do that, and it is the promise a browser can rely on.
9. Does the sentinel scheme stay private to `Platform.Identity`, out of `SchemeNames`? **Rec: yes.**
   Why: no other workstream names it.
10. Is "G0 report" the term for the gate's verdicts and answers, leaving "finding" to the Evidence
    tab's sense? **Rec: yes.** Why: in the Evidence tab "finding" means a probe-confirmed behaviour
    with an id.
11. At the freeze, do the schemas flip to `frozen-at-g1b` (dropping the root `exceptions`), do the
    `[Freeze("G1b")]` markers go while `FreezeAttribute` stays, and are the three freeze tests
    rewritten to assert an empty draft set? **Rec: yes.** Why: it mirrors G1a's shape and keeps the
    mechanism for Phase 2.
12. Is `contracts-v1` an annotated tag on I-6's `main` merge, naming G1b, ADR-0014 and the green CI
    run? **Rec: yes.** Why: that is the `skeleton-v1` convention, and gate state is read from tags.
13. If Cognito's grace window does not cover a replayed refresh token, does task 3.8 adopt the
    reserve design behind an unchanged `IAccessTokenSource`? **Rec: yes.** Why: ADR-0005 already
    names this trigger, and the interface hides the swap.
14. If a Cognito refresh token is longer than 4,096 characters, is item 4's way out taken (one
    in-house class over Duende.IdentityModel) and ADR-0005 superseded? **Rec: yes.** Why: it is the
    plan's own fallback, behind the same interface.
15. If `aud` is lost on a refresh without `resource`, does 3.8 send `resource` on every refresh, and
    if Cognito refuses that, is resource binding declared unsupported for the `cognito` profile with
    `oauth.resource` kept in the schema? **Rec: yes.** Why: upstreams already check `client_id` and
    scope, and app2 shows that an entry without `resource` is valid.
16. If Cognito's discovery lists no revocation endpoint, does `IIdpProfile` gain an explicit member
    that supplies one, frozen at G1b? **Rec: yes.** Why: T-B14 shows the library's revoke throws
    without one, and adding the member after the freeze needs a change request.
17. If revoking a refresh token does not revoke its successor, does 3.9 revoke the token it reads
    after any in-flight refresh, with no contract change beyond question 8? **Rec: yes.** Why: the
    browser-facing promise is already the session's removal, and T-B18's race is 3.9's to handle.
18. If Cognito refuses a callback URL with a port, does I-6 bind 443, drop `:8443` from the
    platform-config example, and open follow-up tickets for 5.5 and 1.8? **Rec: yes.** Why: that is
    the plan's way out, and `fixtures/` and `infra/` are not I-6's paths.
19. If item 2 fails with no fix inside 3.0, does I-6 stay open and untagged, with a new ticket
    comparing Finbuckle, Duende BFF v4 and a tenant-aware stack? **Rec: yes.** Why: freezing on a
    failed mechanism would let 3.1 to 3.9 start on it.
20. Does I-6 also fix the stale ADR counts (`docs/agents/domain.md`, `CLAUDE.md`,
    `docs/adr/README.md`) and `AGENTS.md:90`? **Rec: yes.** Why: these are integrator paths I-6
    touches anyway, and stale state misleads later sessions.

## Proposed glossary / ADR

Proposed `GLOSSARY.md` terms, under "Work, gates and evidence". Not applied; they wait for Adam.

- **G0 report**: the verdict on each of Gate G0's five items and the answers to item 2's five
  questions, against a real Cognito pool. What G1b folds into the identity contracts. _Avoid_: G0
  findings.
- **Finding**: a behaviour a probe in a feasibility harness confirmed, with an `R-` or `T-` id, and
  what the plan does about it. _Avoid_: using it for a gate's verdict.
- **Way out**: the alternative the plan names in advance for a gate item that fails, so that taking
  it needs no new decision.
- **Draft part**: a piece of a contract that may change without a change request until its gate is
  tagged. The identity parts are draft until G1b. _Avoid_: "provisional", "unfrozen".
- **Freeze**: the point, marked by a gate's tag, after which a contract changes only through a
  change request. G1a froze the skeleton, and G1b freezes the identity parts.
- **Grace window**: the period after a refresh during which the provider still accepts the
  rotated-out refresh token. Cognito's is configured to 10 seconds. _Avoid_: "reuse interval".

ADR candidates (all three criteria checked):

- **ADR-0014: Freeze the identity contracts on the G0 report.** It is hard to reverse, because scheme
  names set data-protection purposes, a frozen interface changes only by change request, and a rename
  invalidates every issued session cookie. It is surprising without context, because identity froze
  separately and later than the rest of the skeleton, and whichever of Q13 to Q18 fired explains
  shapes a reader would otherwise "fix". It is a real trade-off: freeze now against carrying identity
  as draft through tasks 3.1 to 3.9, and per branch, library against reserve or in-house refresh, and
  binding against no binding on Cognito. Status lines on ADR-0001, 0005 and 0011 point to it.
- **Conditional: superseding ADR-0005** if question 14 fires (a refresh token over 4,096
  characters). Swapping the token library for an in-house class over Duende.IdentityModel meets all
  three criteria, but ADR-0014 can carry it. A separate ADR only if Adam prefers one decision per
  file.

## Comments

**Overnight collect, 2026-10-17:** Status was `ready-for-human`, now `resolved`. Task I-6 landed in `ad5e041` ("Merge task I-6: decide the stage order (ADR-0018) and close the freeze"), and the annotated `contracts-v1` tag that closes G1b is reachable from tonight's base `eb65be8`.
