# 6.3a — The negative rows that need no session

Status: ready-for-agent
Owner: workstream 6 (Verification and operations)
Parent row: 6.3 (Negative suite, hermetic: N-01 to N-25 and N-27 — green at G4)
Base: trunk, after tasks 2.3b and 4.3b

## Review amendment — 2026-10-15

The original carve included stripping owned by unlanded plan row 4.2. The deliverable is twelve
distinct rows: six whole (N-08, N-09, N-13, N-14, N-16, N-20) and six partial
(N-07, N-10, N-12, N-24, N-25, N-27). N-11 is absent until 4.2; N-12 proves CORS/CSP treatment on 200/500, with
`Set-Cookie` stripping deferred to 4.2. Session-dependent clauses still wait for G4. The user
stories below preserve the original target; this amendment and the scope list govern delivery.
N-24 lacks the successful BFF-response clause, N-25 lacks real-browser `__Host-` enforcement, and
N-27 lacks the proof that a live session survives `/signout-oidc`; those clauses remain outstanding.

## Problem Statement

The platform's refusals are implemented but unproven in the composed gateway.

Row 6.3 asks for the whole negative suite and is marked done only when it is green at G4, because
thirteen of its rows need a session and nothing in `Platform.Identity` may start before
`contracts-v1` is tagged. So the row sits blocked as a unit. Meanwhile fourteen of its rows rest
entirely on work that has already landed: host resolution, the asset path rules, the security
headers, the request gate, the forwarded-headers stage and the telemetry line.

The consequence is a verification hole with a long lifetime. Each of those refusals is proven today
only in its owner's own test project, against doubles. Nothing yet proves that the refusal survives
composition — that stage 4 really does answer 404 before routing, that stage 5's headers really are
on an asset response, that a rule id written at stage 6 really does reach the request line. A
regression in any of them would be invisible until G4, which is several gates away, and whoever hit
it then would be debugging a stage they did not write.

## Solution

Carve a sub-row that proves, in the composed gateway, every negative row that does not need a
session. It runs hermetically on every change from today, and it grows into row 6.3 rather than
replacing it: at G4 the identity rows join the same project and the same harness.

Each row is proven as a browser or a raw client sees it — a status code, a header, a body, a log
line, or what an upstream received — never by reaching into the stage that produced it.

## User Stories

1. As a platform integrator, I want the negative rows that need no session proven in the composed
   gateway today, so that a refusal that works in its owner's project cannot quietly stop working
   once the stages are composed.
2. As a platform integrator, I want each proven row named by its `N-` id in the test, so that I can
   read the suite against the Tests tab without a translation step.
3. As a workstream 2 owner, I want an unknown host to be refused with 404 before routing runs, so
   that I learn immediately if host resolution starts admitting a host the registry does not name.
4. As a workstream 2 owner, I want the right host on the wrong port refused, so that the authority
   comparison stays exact rather than host-only.
5. As a security reviewer, I want a spoofed `X-Forwarded-Host` to change nothing about which app
   serves a request, so that an attacker cannot select another tenant's logical boundary with a
   header.
6. As a security reviewer, I want `X-Forwarded-Proto` and `X-Forwarded-For` from an address outside
   the trusted networks to be ignored, so that a client cannot claim HTTPS or forge its own address.
7. As a security reviewer, I want asset paths built from dot segments, encoded slashes, backslashes,
   doubled slashes and null bytes to be refused, so that no object outside the app's release prefix
   can ever be read.
8. As a security reviewer, I want a refused asset path to be refused before any object store read is
   attempted, so that a traversal attempt leaves no trace of a lookup outside the prefix.
9. As an app owner, I want my app's content security policy on every response my app serves, so that
   another app's policy can never be applied to my shell or my assets.
10. As an app owner, I want framing, sniffing and cross-origin-resource headers present on a shell,
    on an asset and on an error response alike, so that there is no response shape that silently
    omits them.
11. As a security reviewer, I want an upstream's `Set-Cookie`, its CORS headers and its own content
    security policy stripped on a 200 and on a 500 alike, so that an upstream cannot reach the
    browser with headers the platform did not sanction.
12. As a security reviewer, I want a cross-site or absent `Sec-Fetch-Site`, and a foreign `Origin`,
    refused on the API path, so that the request gate's pre-authentication refusals are proven in
    composition.
13. As a security reviewer, I want no `Access-Control-Allow-*` header on any response, including on a
    preflight, so that the platform never becomes a CORS-reachable API.
14. As a security reviewer, I want an API name the app does not list to be refused with 404, so that
    an app cannot reach an upstream it never declared.
15. As a security reviewer, I want paths built to escape the API prefix refused, so that the proxy
    cannot be walked onto another upstream's routes.
16. As an upstream owner, I want the gateway to prove that a caller's cookies, bearer tokens, CSRF
    header and forwarding headers never reach me, so that I can trust what my own authorization
    reads.
17. As a security reviewer, I want `TRACE`, `CONNECT`, an unsafe method on an asset path and
    oversized headers all refused, so that the method allowlist and header limits hold in
    composition.
18. As a security reviewer, I want the platform cookie names refused in other letter cases, so that
    a sibling host cannot smuggle a cookie past a case-sensitive name check.
19. As a security reviewer, I want `/signout-oidc` to answer 404 on both `GET` and `POST`, so that a
    provider-shaped path cannot be used to disturb the platform before the identity stages exist.
20. As a registry publisher, I want a document with a duplicate host, a disallowed upstream or a
    forbidden policy source rejected whole, so that a bad publish never partially applies.
21. As a registry publisher, I want the live snapshot unchanged after a rejected publish, so that a
    bad document cannot degrade what is already serving.
22. As an operator, I want the full suite run with canary values for tokens, secrets, cookies, codes
    and state, and every log line searched, so that I know no credential can reach a log.
23. As an operator, I want the canary sweep to cover every line the suite produced, not a sampled
    subset, so that the absence it reports is a real absence.
24. As a workstream 4 owner, I want every refusal to carry the rule id the contract assigns it, so
    that the telemetry a run produces can be read against the rule table.
25. As a workstream 4 owner, I want rule ids asserted through the shared constants rather than string
    literals, so that renaming an id is a compile error instead of a silent test gap.
26. As an agent working later rows, I want the suite to use the landed harness rather than its own
    doubles, so that there is one description of how the gateway boots in tests.
27. As an agent working later rows, I want host-level rows driven over a raw socket, so that a
    spoofed or mismatched authority actually reaches the process instead of being answered by a
    proxy.
28. As a maintainer, I want each row's test to fail for the reason the row names, so that a red test
    tells me which refusal broke rather than only that something did.
29. As a maintainer, I want the rows that need a session absent from this suite rather than skipped,
    so that a skip count never stands in for coverage.
30. As a maintainer, I want the partial rows to state in the test which clause is proven and which
    waits for G4, so that nobody reads a half-proven row as a whole one.
31. As the integrator, I want this sub-row to leave `src/` untouched, so that a verification task can
    never become an unreviewed behaviour change.
32. As the integrator, I want any missing or wrong refusal reported as a change request rather than
    fixed in place, so that the ownership rule holds even when the fix looks like one line.

## Implementation Decisions

- **One seam, and it already exists.** Every row is driven through the gateway test harness that
  landed with task 6.1: it boots the real gateway entry points over an in-memory server against the
  test identity provider, the real registry over an in-memory object store, the real mock API on two
  in-process listeners and an echo upstream, with both apps seeded from the SPA fixtures. No new
  test double, no new abstraction in `src/`, and no second way to boot a gateway in tests.
- **The suite lands in the negative-suite test project that is already in the solution** —
  `Gateway.SecurityTests`, owner workstream 6, today empty and already carrying the comment
  "Negative suite, N-01 to N-27". It references the harness and nothing else, so no project,
  package or solution edit is needed and no integrator path is touched.
- **Four assertion surfaces, chosen per row:** the test browser (cookie jar per host, Fetch Metadata
  headers, redirect control) for browser-shaped rows; the raw HTTP client for rows where the
  authority or a malformed request line is the point; the captured log for rule ids, the request line
  and the canary sweep; and the mock API and echo upstream for what an upstream received.
- **Host-level rows go over the raw socket, not the browser.** A spoofed or mismatched authority must
  reach the process, and an HTTP client that answers on the `Host` header would never deliver it.
- **Rule ids come from the shared constants** in `Platform.Abstractions` (ADR-0015), never as string
  literals. A renamed id must break the build, not pass silently.
- **Rows in scope, whole:** N-08, N-09, N-13, N-14, N-16, N-20.
- **Rows in scope, partially, with the proven clause named in the test:** N-07 (the pre-authentication
  refusal on the API path and the absence of CORS headers; the authenticated variants wait for G4),
  N-10 (the unlisted API name and the escape paths; "no upstream logs the request" is proven against
  the echo upstream and the mock API as they stand), N-12 (CORS and upstream CSP treatment on
  success and error responses; `Set-Cookie` stripping waits for row 4.2), N-24 (security headers
  on shell, asset and errors; successful BFF response pending), N-25 (case-sensitive server
  validation; real-browser `__Host-` enforcement pending), N-27 (404 on GET/POST; live-session
  survival pending).
- **Deferred to row 4.2:** N-11 request stripping and N-12 response-cookie stripping. N-11
  token injection also needs the identity work; no passing assertion claims these clauses today.
- **Rows deferred to row 6.3 at G4, and absent from this suite rather than skipped:** N-01 to N-06,
  N-15, N-17, N-18, N-19, N-21, N-22, N-23. N-26 belongs to the deployed browser suite.
- **A temporal caveat recorded in the suite itself.** The shell gate is stage 8 and belongs to
  workstream 3, so it is not in the composed pipeline yet: assets are currently served without a
  session. Every row here that reaches an asset or an API path today does so because that stage is
  absent, and the same rows must be re-read when it lands. The suite says so where it matters, so
  that a later session does not mistake a changed status code for a regression.
- **No behaviour changes.** If a refusal is missing, carries the wrong rule id, or contradicts its
  contract, the finding goes into a change request under the contracts directory and the row stops
  there. Workstream 6 owns the proof, not the stages.
- **This sub-row cannot close row 6.3.** The roadmap generator treats a split id as never landing its
  parent row, and row 6.3's own done-when is "green at G4". The merge subject names 6.3a only.

## Testing Decisions

- **A good test here asserts only what a client outside the gateway can observe:** the status code,
  the response headers, the body, the log lines the run produced, and what an upstream received. No
  test calls a stage class, constructs a middleware, or reads a private field. A test that would need
  to do so is evidence that the row belongs in its owner's project instead.
- **Modules under test:** the composed gateway, which is to say the registry, hosting, proxy,
  security and telemetry projects as `Program.cs` wires them. None of those projects is referenced
  directly; the harness is the only reference.
- **Prior art to follow:** the harness tests that landed with task 6.1 — the boot tests, the registry
  tests, the test-browser tests and the load-balancer transport tests — for how to start a harness,
  seed a registry, publish a release and drive a browser. For plan-time and refusal-shaped
  assertions, the per-project refusal tests in the hosting, security and proxy test projects show the
  rule-id conventions; this suite proves the same ids again in composition rather than restating
  those tests.
- **One harness per test class**, disposed asynchronously; rows that need a different registry or a
  different trusted-network set take their own harness options rather than mutating a shared one.
- **The canary row is a whole-suite property, not a single test.** Canary values are supplied through
  the harness's configuration and the captured log is swept at the end of the run, so the row's claim
  covers every line the suite produced.
- **Each test is watched failing first**, for the reason the row names, before the assertion is made
  to pass — the discipline the two lanes that landed tonight already used. Where a row's refusal is
  already implemented and therefore green on arrival, the test is mutated to prove it can fail: the
  mutation and its observed failure count are recorded in the ticket's comments.
- **The suite is hermetic.** No network, no credentials, no AWS, no browser binary. It runs in the
  same gate as every other test project.

## Out of Scope

- Every row that needs a session: the identity rows of row 6.3 and anything behind `contracts-v1`.
- The deployed tier. Nothing here runs through the tunnel, the load balancer or a real bucket, and
  the D-marked clauses of the rows in scope are not attempted.
- N-26 and the third-SPA scenario, which belong to the deployed browser suite in row 6.5.
- The functional rows. F-06 and F-11 are carved separately as 6.2a.
- Fixing any defect the suite finds, in any stage. The finding is a change request.
- Closing row 6.3, or tagging any gate.
- Any edit to `src/`, to the solution file, to the central package versions, or to any contract.

## Further Notes

- The shared rule-id constants landing on trunk is what makes this worth doing now: a refusal's id is
  checkable against a single list rather than against a reviewer's memory of the rule table.
- The rows in scope were chosen by asking which clause of each row rests only on landed work, not by
  which row looked testable. Three rows are therefore partial, and saying so in the test is part of
  the deliverable — a row recorded as whole when half of it is unproven is worse than a row left out.
- When the shell gate and the `/bff` endpoints land, the natural next carve is the identity half of
  these same rows, in this same project, against this same harness. Nothing here should need
  rewriting for that; if it does, the seam was wrong.
