# ADR-0038: Fragments compose into one shell at one host, each with its own client and release

- **Status:** Proposed
- **Date:** 2026-11-17
- **Deciders:** Adam, on the fragments map (#765) and its tickets #766 to #776; the spec #961,
  accepted in https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645455906
- **Plan ref:** No decision row; roadmap ladder stage S8a, gate G9. **Amends** ADR-0024 decision 1
  and ADR-0001's decision. Extends ADR-0009's decision.

## Context

ADR-0001 gives a host one `oidc:{appId}` scheme and one client. Teams want units with their own client (ownership, audience) and their own release, composed into one SPA at one host, without tokens in the browser. The provider session is one per browser (ADR-0017, C-2.6), and Cognito's managed-login session lasts one hour.

## Decision

1. **One host, N clients, one issuer.** A shell's registry entry nests `fragments[]`; each fragment has its own `oauth` block (the frozen `$defs/oauth`), `release`, `apis`, narrowed `csp` and `entry`. A fragment's scheme is `oidc:{appId}:{fragmentId}`, signing in to the shell's `cookie:{appId}`, on its own callback path `/signin-oidc/{fragmentId}`. This extends ADR-0001: a host resolves one shell and its fragments' schemes, the shell's first.
2. **Identity binds by namespace, content by path.** A fragment's APIs live under `/fragments/{fragmentId}/api/{name}/` forwarding that fragment's token; its content under `/fragments/{fragmentId}/**` from `apps/{appId}/fragments/{fragmentId}/releases/{release}/`. The boundary between fragments is logical, not isolation.
3. **Acquisition is eager.** The shell's login chains one silent code flow per fragment, merging each set into the one session record; a definitive refusal marks the fragment `unavailable` once written, and the shell still loads. The fallback is user-initiated. One logout revokes every set by its own scheme.
4. **One session record holds N token sets**, each with its own expiry, refresh token, session gate and refresh lease. This amends ADR-0024: "one lease per session key" becomes "one lease per token set"; `IRefreshLeases` keeps its signature.
5. **A fragments document is `schemaVersion: 2`.** The schema accepts `[1, 2]`; a document with any fragment declares 2, so a pre-fragments build refuses it by version (ADR-0019 decisions 1 and 2).
6. **An `assets/` URL identifies the same bytes in its namespace across every release**, enforced by write-once SHA-256 claims; fragment activation is request-time with no one-release-per-document guarantee.

## Alternatives rejected

*Top-level fragment entries naming their shell*: the named alternative, which per-team ownership (S10) may yet force; the migration is a later `schemaVersion` under ADR-0019. *N hosts with one client each*: what the platform does today; a second origin and visible logins. *A shared `/signin-oidc` with `SkipUnrecognizedRequests`*: dispatch by state in provider order, and an unrecognised callback falls to a 404. *Flat `apis[].fragment` in one namespace*: API names become a cross-team convention. *A request header choosing the client*: invisible in the registry and the route table. *Lazy acquisition*: past Cognito's one-hour managed-login session, every fragment's first use would prompt. *Gates and leases per record*: N serial refreshes reach 2 × TTL and report provider latency as a store failure. *Keeping `schemaVersion: 1`*: a pre-fragments build would refuse a document it shares by shape, not by version.

## Consequences

N + 1 clients per shell count against Cognito's 20 managed-login styles per pool; a `Policy:` cap (placeholder 5, from 4 + 3N redirects against the browser's 20) and `MergeBudget` (placeholder 3) are set by G9's sitting. Tokens no current credential can revoke are dropped unrevoked and logged. The record's shape leaves room for S9's token exchange.

## Evidence

Branch `prototype/fragment-dispatch`, commit `9fa66d0`, 42 checks against TestIdp; research branches `research/fragment-scheme-dispatch`, `research/cognito-app-clients-one-pool`, `research/testidp-n-clients-one-host`, `research/runtime-composition-same-origin`; G0 C-2.6, C-2.14, C-2.19; G9's sitting records for O1 and O2 once made.
