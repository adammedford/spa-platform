# 6.2a — The functional rows that need no session

Status: ready-for-agent
Owner: workstream 6 (Verification and operations)
Parent row: 6.2 (Functional suite F-01 to F-11, hermetic — green at G2, except F-07 at G5)
Base: trunk, after tasks 2.3b and 4.3b

## Problem Statement

Two of the functional rows describe behaviour that is fully landed and entirely unproven in
composition.

Row 6.2 is blocked behind `contracts-v1` because nine of its eleven rows begin with a signed-in user.
But F-06 — per-app content security policy, the asset fallback matrix, and switching a release and
switching back — depends only on hosting, the security headers and the registry, all of which are in
the tree. F-11 — one trace id shared by the gateway's log line, its spans and the `traceparent` the
upstream received — depends only on the telemetry stage and the proxy, both landed, and its own
Tests-tab entry already names no gate dependency.

So the two claims the platform most often needs to make to an app owner — "your policy is yours
alone" and "content changes with no deployment" — rest today on the assertion of the stages that
implement them, not on an observation of the gateway serving two apps at once.

## Solution

Carve a sub-row proving F-06 and F-11 in the composed gateway, hermetically, from today. Both rows
are proven exactly as their Tests-tab entries word them, as a browser and an upstream observe them,
and both join row 6.2 unchanged when the identity rows arrive at G2.

## User Stories

1. As an app owner, I want my content security policy served with my app and no other app's, so that
   a second tenant on the same gateway cannot alter how my shell is allowed to load.
2. As an app owner, I want my policy to be the platform baseline plus my own additions, so that I can
   read my entry and know what the browser will enforce.
3. As an app owner, I want an entry that tries to add a directive outside the sanctioned set to be
   refused rather than partially honoured, so that the baseline cannot be widened by a publish.
4. As an app owner, I want two apps on one gateway to receive different policies in the same run, so
   that the per-app claim is proven by contrast and not by a single observation.
5. As an SPA developer, I want an extensionless path that accepts HTML to return my shell, so that
   client-side routes survive a full page load.
6. As an SPA developer, I want a request for a file that does not exist to return 404 rather than my
   shell, so that a broken asset reference is visible instead of silently serving HTML.
7. As an SPA developer, I want a path with an extension to be served as that asset or refused, never
   rewritten to the shell, so that the fallback rule has an edge I can reason about.
8. As an SPA developer, I want `HEAD` on an asset to behave as `GET` does without a body, so that a
   cache or a probe sees the same decision.
9. As an operator, I want to switch an app's live release by publishing a registry document, so that
   a rollout needs no deployment.
10. As an operator, I want to switch the release back and see the previous content again, so that a
    rollback is as cheap as a rollout.
11. As an operator, I want the content to change with no restart and no new task, so that the "no
    deployment" claim is observed rather than assumed.
12. As an operator, I want a release switch to leave the other app untouched, so that one app's
    rollout cannot disturb its neighbour.
13. As an operator, I want each app's cached per-entry state to be evicted when its fingerprint
    changes and left alone when it does not, so that a switch is both effective and narrow.
14. As an operator, I want the registry's revision to be observable after a switch, so that I can
    tell which document is live.
15. As an operator following a single API call, I want the gateway's log line, its spans and the
    `traceparent` the upstream received to carry one trace id, so that I can follow a request across
    the hop without correlating by timestamp.
16. As an operator, I want that request line to name the app, so that a trace in a multi-tenant log
    is attributable.
17. As an operator, I want the upstream to receive W3C trace context rather than a vendor header, so
    that an upstream I do not own can join the trace.
18. As an operator, I want a second, concurrent call to carry a different trace id, so that I know the
    id identifies a request and not the process.
19. As an operator, I want the span the proxy emits to be a child of the request's span, so that the
    hop is visible as a hop.
20. As a workstream 6 owner, I want spans and metrics read through the in-memory reader the telemetry
    contract already prescribes, so that no collector has to run in a test.
21. As a workstream 6 owner, I want the telemetry assertions to name the contract's field list, so
    that a field added or renamed without a contract change fails a test.
22. As an agent working later rows, I want these rows to use the landed harness rather than their own
    doubles, so that there is one description of how the gateway boots in tests.
23. As a maintainer, I want F-06's four clauses asserted separately, so that a red test names which
    clause broke.
24. As a maintainer, I want the rows that need a session absent rather than skipped, so that a skip
    count never stands in for coverage.
25. As the integrator, I want this sub-row to leave `src/` untouched, so that a verification task
    cannot become an unreviewed behaviour change.
26. As the integrator, I want any contradiction between a stage and its contract reported as a change
    request rather than fixed, so that the ownership rule holds.

## Implementation Decisions

- **The same single seam as the negative carve:** the gateway test harness from task 6.1. It already
  seeds two apps from the SPA fixtures, publishes releases into the in-memory object store, refreshes
  and republishes the registry, and exposes the captured log. Nothing new is needed to prove either
  row.
- **The suite lands in the functional-suite test project already in the solution** —
  `Gateway.IntegrationTests`, owner workstream 6, which already carries the comment "Functional
  suite, F-01 to F-11" and the harness reference. The task 6.1 harness tests live there under their
  own folder; these rows take a sibling folder. No project, package or solution edit.
- **F-06 is proven in four separate clauses**, each with its own test: per-app policy observed on both
  apps in one run and contrasted; the extensionless-HTML fallback returning the shell; a missing asset
  returning 404; and content changing after a registry publish with no restart. The release switch is
  driven by publishing a registry document through the harness, which is what an operator does.
- **The "no deployment" clause is asserted as an invariant, not a narration:** the harness's gateway
  instance, its service provider and its registry poll interval are unchanged across the switch, and
  the assertion says so. The in-process equivalent of the deployed row's "task id and start time are
  unchanged".
- **F-11 is proven through the in-memory span and metric readers** the telemetry contract prescribes,
  plus the echo upstream's view of the request headers. One API call, then: the trace id on the
  request log line, the trace id of the request's span, the parent-child relation of the proxy's span,
  and the trace id parsed out of the `traceparent` the upstream received are all compared to one
  value. A second concurrent call must carry a different one.
- **The request line's fields are asserted against the telemetry contract's list and order**, which
  is the same list task 6.4a's lane used when it added `rule` and `session_ref`. `session_ref` is
  expected null here, because there is no session — stated explicitly rather than left unasserted.
- **Instrument names are asserted to be the contract's `Platform.<Project>` form**, plus the proxy's
  own source, so that an instrument named outside the convention cannot be collected by accident.
- **A temporal caveat recorded in the suite.** The shell gate is stage 8 and belongs to workstream 3,
  so assets and the API path are currently reachable without a session. Both rows rely on that today.
  When the shell gate lands, F-06's requests become authenticated navigations and F-11's call becomes
  an authenticated one; the tests say so where it matters, so a later session does not read a changed
  status code as a regression.
- **F-07's onboarding clause is out.** It needs a third app and an identity provider that admits a new
  client, and its own row is marked for G5.
- **No behaviour changes.** A stage that contradicts its contract produces a change request, not an
  edit. Workstream 6 owns the proof.
- **This sub-row cannot close row 6.2**, whose done-when is "green at G2". The merge subject names
  6.2a only.

## Testing Decisions

- **A good test here asserts what a browser, an operator or an upstream can observe:** response
  headers and bodies, status codes, the log lines and spans the run produced, and the headers the
  upstream received. No test reads a stage's internals, constructs a middleware, or asserts on a
  cache's shape. Where a row's claim can only be reached through internals, the claim belongs in the
  owning workstream's project.
- **Modules under test:** the composed gateway — hosting, registry, security, proxy and telemetry as
  `Program.cs` wires them. Only the harness is referenced.
- **Prior art to follow:** the task 6.1 harness tests in the same project, for starting a harness,
  publishing a release, republishing a registry and driving the test browser. For the telemetry
  assertions, the tests that landed with task 6.4a in the telemetry project show how the request line
  and the counters are read; this row reads the same fields from the composed gateway instead of from
  the stage in isolation.
- **Contrast over repetition for the per-app claim.** The policy test asserts two apps' policies in
  one run and asserts that they differ in the entry's additions and agree on the baseline. A single
  app's policy proven twice would not show tenancy.
- **Each test is watched failing first.** For clauses already implemented and therefore green on
  arrival, the test is mutated to prove it can fail — a wrong policy source, a swapped fallback
  decision, a trace id taken from a second call — and the observed failure counts are recorded in the
  ticket's comments.
- **The suite is hermetic.** No network, no credentials, no AWS, no collector, no browser binary.

## Out of Scope

- Every functional row that begins with a signed-in user: F-01 to F-05, F-08, F-09, F-10, and F-07's
  onboarding scenario.
- The deployed tier and every D-marked clause of F-06, including the task-identity assertions that
  only a running service can show.
- The negative rows, which are carved separately as 6.3a.
- Fixing any defect found, in any stage.
- Closing row 6.2, or tagging any gate.
- Any edit to `src/`, the solution file, the central package versions, or any contract.

## Further Notes

- F-11's Tests-tab entry names no gate dependency at all, which is the strongest signal that it was
  never blocked by G1b and has simply been waiting inside a blocked row.
- The release-switch clause is the one an operator will lean on hardest and the one with no unit-level
  equivalent: a stage can prove it serves the release it was told to, but only the composed gateway
  can show that publishing a document is what changes which release that is.
- If the per-app policy assertion turns out to need anything beyond reading a response header, that
  is a finding about the security-headers stage worth a change request, not a reason to reach deeper
  in the test.
