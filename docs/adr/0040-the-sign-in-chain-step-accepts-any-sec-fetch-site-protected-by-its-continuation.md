# ADR-0040: The sign-in chain step is the one /bff path that accepts any Sec-Fetch-Site, protected by its continuation (amends ADR-0007)

- **Status:** Accepted
- **Date:** 2026-11-17
- **Deciders:** integrator, on Adam's decision comment on #983; the spec #961, accepted in
  https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645455906
- **Plan ref:** No decision row. **Amends** ADR-0007's Decision (plan decision D7), its Fetch
  Metadata defence, "`/api` and `/bff` accept same-origin browser requests only", for
  `GET /bff/chain` alone; the other three defences are unchanged. The spec #961, revision 1,
  "`/bff/chain`: the continuation step"; change request #983.

## Context

ADR-0007's cross-app defence is four mechanisms together, and Fetch Metadata is one: "`/api` and
`/bff` accept same-origin browser requests only, judged by `Sec-Fetch-Site` and `Origin`." The
request gate holds it. `contracts/request-gate.md` rejects a missing `Sec-Fetch-Site` on `/api` or
`/bff`, and rejects `same-site` like `cross-site`, because that value is what a sibling SPA
produces.

The fragments spec (#961) composes fragments into one shell at one host, each with its own client,
and the shell's sign-in chains one silent code flow per fragment. Each step of that chain is a
navigation the gateway redirects to itself after the provider's redirect:
`GET /bff/chain?c={continuation}`. Before this decision the path has no row, and an unrecognised
`/bff/*` path answers `path.reserved`.

Fetch Metadata computes `Sec-Fetch-Site` over a navigation's whole redirect chain. A step reached
through the provider's redirect is `cross-site`, or `same-site` under a custom domain sharing the
site, at every hop after the provider. `Sec-Fetch-Mode` and `Sec-Fetch-Dest` survive redirects.
Adam's review of the spec named the gap: copying `/bff/login`'s origin rules is not enough.
Revision 1 of the spec answers it.

## Decision

1. **`GET /bff/chain` accepts any `Sec-Fetch-Site`.** The request gate reads none on that path, so
   a missing one is not a rejection there. It is the one `/bff` path of which that is true. This
   amends ADR-0007's Fetch Metadata defence for this path alone.
2. **The gate still requires the step to be a navigation.** The path accepts `GET` only
   (`gate.method` otherwise) and requires `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document`
   (`gate.navigation` otherwise). It carries no antiforgery token, because a navigation cannot
   carry one.
3. **The step's protection is its continuation, as a callback's is its state.** The step needs the
   host session: the cookie must resolve to a session for this app, and that session's key must
   equal the key the continuation was minted for. A step with no session gets the shell gate's
   usual challenge. The continuation is the query's `c`, and nothing else in the query is read. It
   is protected by the data-protection key ring under a purpose naming the app and `chain`, and it
   carries the session key, the snapshot of the shell's `fragmentId`s in registry order, the
   position of the next fragment, the validated local `returnUrl` from `/bff/login`, and an
   expiry, `ChainLifetime`, five minutes from minting. The caller chooses neither the position nor
   the return URL. A continuation is not single-use. A replayed step does no work twice: at each
   step the gateway skips any `fragmentId` of the snapshot whose token set the session record
   already holds as `signedIn` with the current `clientId`, and a position past the snapshot's end,
   or nothing left to do, ends the chain with a redirect to the continuation's `returnUrl`.
4. **A refused step ends under the new rule id `auth.chain`.** A continuation that is missing,
   unreadable, expired, or minted for another session or another app ends with a 302 to `/`, the
   app's root, with no problem body and no change to the session.
5. **Every other `/api` and `/bff` path keeps its rule.** A fragment's API,
   `/fragments/{seg}/api/**`, takes the `/api/**` rules exactly and falls under ADR-0007's `/api`
   defence. ADR-0007's other three defences are unchanged.

The rule lives in `contracts/request-gate.md` → Rules by path and Rule ids, and the browser-facing
half of the step in `contracts/http-contract.md` → Fragments → "`/bff/chain`".

## Alternatives rejected

- **Give `/bff/chain` the rule `/bff/login` has, `same-origin` or `none`.** Every step after the
  provider's redirect is `cross-site` or `same-site`, so the rule would refuse every legitimate
  step (#961, "Why `/bff/login`'s rules do not fit"; Adam's review of the spec, point 1).
- **Carry the exception in the fragments ADR.** The fragments ADR draft on #961 predates revision 1
  and says nothing about the chain's protection (#983's grilling record, R5.1). Offered to Adam on
  2026-11-09 and not chosen (the session answer recorded on #983).
- **Land the new rule id between gates, by decision.** `contracts/README.md` → "What may land
  between gates" holds a new rule id for the next gate, as `auth.store` waited for G3 (#343,
  c5d6775). Rejected in revision 2 of the spec's ticket graph, item 1.

## Consequences

- A `cross-site` navigation to `/bff/chain` with a valid continuation proceeds. A forged step can
  at most make the victim's own browser run silent logins for its own session's fragments, each
  refused by the `sub` check if the provider session belongs to another user. No `returnUrl` comes
  from the query, so there is no open redirect.
- A refused step discards nothing: the shell's session stands, and the browser lands on `/`.
- A store failure inside a step answers `503 session_store_unavailable`, as on the callback.
- `contracts/request-gate.md` gains the `/bff/chain` row and the rule id `auth.chain`, with no
  problem code, appended after `auth.store`. `gate.navigation` fires for `/bff/chain` as for
  `/bff/login`.
- `Platform.Abstractions` gains `RuleIds.AuthChain`, last in `RuleIds.All`, in the same commit as
  the row, because `RuleIdsTests` fails when `RuleIds` drifts from the table. `Rejection` refuses
  to end `auth.chain` with a problem body, as it refuses `auth.callback`.
- The rule ids go from 13 to 14, and the pinned set of rule ids and problem codes from 27 to 28, or
  to 29 with `fragment_login_required` in the same landing. Workstream 6's count change (#1002)
  lands in the same pull request.
- A new rule id waits for a gate, so the row, the rule id and this ADR land together in landing B,
  after G6's tag (`g6-continuity`, #348): #982, #983, #985 and #988 with #1002's branch.
- ADR-0007's status line records the amendment.

## Evidence

- The spec #961, "`/bff/chain`: the continuation step" (revision 1), accepted in
  https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645455906
- Adam's review of the spec, point 1:
  https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645319680
- Revision 2 of the spec's ticket graph, item 1, the gate timing:
  https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645936379
- #983's grilling record, R5.1:
  https://github.com/adammedford/spa-platform/issues/983#issuecomment-6649780398
- The draft decision on #983:
  https://github.com/adammedford/spa-platform/issues/983#issuecomment-6649787002
- How `Sec-Fetch-Site` is set across redirects: W3C, "Fetch Metadata Request Headers", Working Draft
  of 21 September 2026, https://www.w3.org/TR/2026/WD-fetch-metadata-20260921/. Section 2.3, "The
  Sec-Fetch-Site HTTP Request Header", gives the algorithm that sets the header for a request. The
  value starts as `same-origin`. It becomes `none` when the request is a navigation the user caused
  directly through the user agent, as by typing an address or clicking a bookmark. Otherwise the
  algorithm takes each URL in the request's URL list: one that is not same origin with the request's
  origin makes the value `same-site` when the two are same site, and `cross-site` when they are not,
  which ends the walk. Section 4.1, "Redirects", says the algorithm walks the request's entire URL
  list, and its example ends with a request redirected through another site and back to the first
  origin, whose value "would still be cross-site".
- The hermetic cases are the spec's functional core for `/bff/chain`, with synthetic Fetch
  Metadata: a `cross-site` navigation with a valid continuation proceeds; a non-navigation request
  answers `gate.navigation`; a POST answers `gate.method`; a missing, tampered, expired,
  other-session or other-app continuation lands on `/` under `auth.chain` with the record
  unchanged; no session gets the shell gate's challenge.
- The deployed observation is the spec's O13, which the G9 sitting records: at N = 2 on Cognito in
  each sitting browser, the `Sec-Fetch-Site`, `-Mode` and `-Dest` each `/bff/chain` step receives
  are recorded and the chain completes; a step replayed after the chain, one with another session's
  continuation and one past `ChainLifetime` each land on `/` with the record unchanged. Decision 3
  ends a replayed step at the continuation's `returnUrl`, so O13's replayed step lands on `/` when
  that `returnUrl` is `/`. No `S-` or `R-` evidence id in `docs/plan/07-evidence.md` covers Fetch
  Metadata across a redirect.
