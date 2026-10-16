# ADR-0018: Telemetry is the outermost stage, so every rejection the application answers is logged and counted

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** integrator (task I-7), at G1b
- **Plan ref:** No decision row. `contracts/request-gate.md` says "Each rejection is logged and
  counted under a rule id", and `contracts/changes/6.4-request-telemetry-feature.md` found one that
  was not. Raised as overnight ticket 53 (`.scratch/overnight/issues/53-stage-1-405-is-neither-
  counted-nor-on-a-request-line.md`) on 14 October 2026; grilled on paper on 15 October; the
  recommendations accepted by Adam the same day.

## Context

Change request 6.4 made `UsePlatformTelemetry` the one owner of the request line and of
`security.rejections`: a later stage that rejects leaves its rule id on `IRequestTelemetryFeature`,
and the telemetry stage logs and counts it once the pipeline has returned. One rejection could not
reach that feature. The method allowlist — `TRACE` and `CONNECT` get 405 under `gate.method`, N-20
— runs inside `UsePlatformForwardedHeaders`, which `src/Gateway/Program.cs` called *before*
`app.UsePlatformTelemetry()`. The feature did not exist yet, the write would have been a no-op, and
the request never reached the telemetry stage at all. Checked by running it: a `TRACE` over a raw
socket produced the allowlist's own Warning line carrying `rule=gate.method` and **no** request
line, where a plain `GET /` produced one. So the 405 was logged with its rule id and neither counted
nor on a request line, which the request gate's sentence does not allow.

`contracts/telemetry.md` recorded that as a known gap waiting for a gate, because every way out
moved something frozen at G1a. The ticket listed four, none cheap: move the allowlist behind the
telemetry stage, which changes the frozen order and logs a `TRACE` before refusing it; give the
allowlist its own counter, which contradicts the single-owner decision; narrow the contract's
sentence, which weakens what workstream 6's negative suite reads; or move the allowlist into the
telemetry project, which is an ownership change.

The 15 October grilling found a fifth, cheaper than all four: **swap the first two stages**.
Telemetry outermost; forwarded headers, with the allowlist inside them, second. Two facts make it
safe, and both were read out of the code rather than assumed:

- **The request line carries nothing forwarded headers settle.** `RequestTelemetry.Complete` reads
  the feature's `rule` and `session_ref`, `IAppFeature` for `app_id` and `registry_rev`, the path
  for `route`, the status and the elapsed time. No scheme, no client address, no host. The "Log
  fields" row of `contracts/telemetry.md` is the whole line, and task 6.2a's `ContractText` asserts
  the line's field names against that row.
- **The span's own tags are set before any stage runs.** The ASP.NET Core instrumentation tags
  `url.scheme` and the rest when hosting starts the activity, ahead of the middleware pipeline, so
  what the span records of the raw request is the same whichever of these two stages is first. The
  telemetry stage adds only `app_id` and `route`.

The first run's option 1 — move the allowlist *later*, into the request gate — was found defective
on the way: `Health.TryAnswerAsync` answers `/healthz` on any method and the gate is unreachable on
an unknown host, so `TRACE /healthz` would become a 200 and N-20 would break.

## Decision

**1. `UsePlatformTelemetry` is the first stage and `UsePlatformForwardedHeaders` the second.**
`contracts/pipeline.md` rows 1 and 2 swap, `src/Gateway/Program.cs` with them, and the pipeline
table names the method allowlist in the forwarded-headers row, which no contract did before. The
orderings paragraph gains the reason: telemetry is outermost so that every rejection the
application answers reaches the line and the count, and it can be outermost because the line
carries no forwarded value.

**2. The allowlist writes `gate.method` to the feature like every other rejecting stage.** That is
one line in `src/Platform.Security/MethodAllowlist.cs` and an assertion in workstream 4's
`RuleIdTelemetryTests`, which are workstream 4's and go in its ticket. Until that lands, the 405's
request line carries `rule: null`: the line exists from this commit, the id arrives with the
ticket, and the composed assertion that it reaches `security.rejections` is workstream 6's and
waits on workstream 4's.

**3. "Rejection" is bounded to what the application answers.** `contracts/request-gate.md` now
says so: a rejection is a response some stage ends the request with. What Kestrel refuses before
any stage runs — the 431 for oversized headers, already in `contracts/http-contract.md` and N-20 —
is the server's answer and carries no rule id. The sentence was always read that way; now it says
it.

**4. Outside `contracts/pipeline.md`, a stage is named, never numbered.** The number is a row's
position in that one table and this decision moved two of them, so every "stage 1" and "stage 2"
in the tree started naming the other stage. `CODING_STANDARDS.md` carries the rule and `GLOSSARY.md`
defines *stage* with it. This commit sweeps the integrator's files; each workstream's are its own,
in one ticket each for workstreams 2, 4 and 6. `docs/adr/0008` keeps "stage 7 (shell gate)" because
it cites `docs/plan/01-plan.md`'s own request-path numbering, which is a frozen export.

**5. It lands at G1b.** `contracts/README.md` says a changed stage waits for the next gate, and
G1b is that gate. The telemetry contract's gap paragraph is replaced by the rule; `contracts/
changes/6.4-request-telemetry-feature.md` carries one pointer line here rather than a new request.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Leave the gap recorded and uncounted | A rule the negative suite loops over (`RuleIds.All`) with one permanent exception is a loop plus a special case, in the one suite whose job is to be adversarial. |
| Move the allowlist behind the telemetry stage, inside the request gate | `Health.TryAnswerAsync` answers `/healthz` on any method and the gate never runs on an unknown host, so `TRACE /healthz` becomes a 200: N-20 breaks. |
| Give `MethodAllowlist` its own `security.rejections` counter | Contradicts change request 6.4's "one owner counts this metric, so the count equals the request lines carrying a rule", and exports a second stream under a second scope. |
| Narrow `contracts/request-gate.md`: "a first-stage rejection is logged and not counted" | Turns the omission into a decision, at the cost of a sentence workstream 6 reads as a guarantee. The swap keeps the guarantee for less. |
| Move the allowlist into `Platform.Telemetry` | An ownership change for a method check, and it leaves the forwarded-headers stage first with nothing to show for it. |
| Keep stage numbers outside the table and renumber by hand | About a hundred mentions in some forty files across four owners, each a copy of the table; a plain swap leaves "stage 1" meaning two different stages in the tree agents read, and rots again on the next reorder. |

## Consequences

- `src/Gateway/Program.cs` changes for the first time since G1a. The composition root is still the
  contract's: the order in the table and the order in the file agree, and they moved together.
- A `TRACE` is now logged and traced before it is refused. That is one request line and one span
  for a request the gateway answers in under a millisecond, which is what every other rejection
  already costs; the allowlist's own Warning line stays, so nothing an operator could see before is
  gone.
- Workstream 4: write `gate.method` to the feature in `MethodAllowlist`, assert it in
  `RuleIdTelemetryTests`, replace the "known gap" text there, and name stages in `Platform.Security`
  and `Platform.Proxy` and their tests. Workstream 6: assert the composed 405's line carries the id
  and is counted, replace the "known gap" text in `RequestTelemetry.cs`, and name stages in the
  telemetry project, the harness and both composed suites. Workstream 2: name stages in
  `Platform.Hosting`, `Platform.Registry` and their tests. Workstream 3: the stub's two mentions go
  when task 3.1 opens the file. Each is a ticket in `.scratch/overnight/issues/`, and task 6.3's
  acceptance gains the composed line and both counters for N-20.
- `tests/Gateway.SecurityTests/Negative/MethodAndLimitTests.cs` asserted the gap — no request line
  for the 405 — and would fail on this order, so its assertion flips in the same pull request, as a
  workstream 6 commit beside this integrator commit. It asserts the line and its status now, and
  leaves the id to the two tickets above.
- Nothing in the identity contracts moves. This is the last G1b prerequisite ADR-0017 named, so
  `contracts-v1` can be tagged on the merge that lands it.

## Evidence

- The gap, measured: ticket 53's `TRACE /x` over a raw socket on 14 October 2026 produced one
  `Platform.Security.MethodAllowlist` line and no `Platform.Telemetry.Request` line.
- What the line reads: `src/Platform.Telemetry/RequestTelemetry.cs` → `Complete` and `RequestLine`;
  the field set is the "Log fields" row of `contracts/telemetry.md`, asserted by
  `tests/Gateway.IntegrationTests/Functional/ContractText.cs` and `ApiTelemetryTests`.
- Where the span's tags come from: OpenTelemetry's ASP.NET Core instrumentation enriches the
  activity hosting starts before the pipeline runs
  (https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNetCore/README.md);
  `PlatformTelemetryExtensions.AddPlatformTelemetry` sets only `url.query` to null there.
- The new order, proven composed: `MethodAndLimitTests.A_method_no_path_accepts_is_405_under_gate_method_and_on_the_request_line`
  reads one request line with status 405 for `TRACE` and for `CONNECT`, which the previous order
  could not produce.
- Why option 1 fails: `src/Platform.Hosting/Health.cs` → `TryAnswerAsync` matches on path alone.
