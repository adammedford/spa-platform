# 53: G1b — The stage-1 405 is logged but neither counted nor on a request line
**Task:** CR-6.4a
**Owner:** integrator
**Blocked by:** None
**Status:** resolved

## Answer

Decided 2026-10-15 by the integrator, on Adam's acceptance of Q105–Q116: **swap the first two
stages**, the fifth option the grilling found. `UsePlatformTelemetry` is now first and
`UsePlatformForwardedHeaders` second, so the allowlist's 405 reaches the request line and
`security.rejections` like every other rejection. Recorded as ADR-0018 on branch
`integrator/stage-order-g1b` (commit `abcfd9c`, with the ws6 assertion flip in `b795a0c`): pipeline
and Program.cs swapped, request-gate.md bounds "rejection" to what the application answers,
telemetry.md's gap paragraph replaced by the rule, stages named not numbered outside the pipeline
table (CODING_STANDARDS.md, GLOSSARY.md). Follow-ups: tickets 62 (ws2 sweep), 63 (ws4: the
allowlist's feature write, plus its sweep) and 64 (ws6: the composed rule and counter assertion,
plus its sweep). This was the last G1b prerequisite ADR-0017 named.

**What to decide:** `contracts/request-gate.md` says "Each rejection is logged and counted under a
rule id". One rejection is not: the 405 for a method no path accepts (`gate.method` from
`src/Platform.Security/MethodAllowlist.cs`, task 4.6).

The cause is the stage order, not a bug in any task.
`src/Platform.Security/PlatformSecurityExtensions.cs` installs the allowlist inside
`UsePlatformForwardedHeaders`, and `src/Gateway/Program.cs` calls that before
`app.UsePlatformTelemetry()`. So the allowlist is **stage 1** and stage 2 has not run: there is no
`IRequestTelemetryFeature` to write and no request line to write it on. `MethodAllowlist`'s own
comment says the placement is deliberate — "checked in stage 1 so a method no path accepts never
reaches a later stage", for N-20.

Established by running it, on the composed gateway on 14 October 2026. A `TRACE /x` over a raw socket
produced exactly one line:

```
{"level":"warning","msg":"No path accepts the request's method. rule=gate.method",
 "category":"Platform.Security.MethodAllowlist","rule":"gate.method"}
```

and no `Platform.Telemetry.Request` line, where a plain `GET /` on the same run produced one.

So the rejection is logged with its rule id, and it is not counted in `security.rejections` and does
not appear on a request line. Nothing regressed — nothing counted that metric before — but the
decision on change request 6.4 makes stage 2 the single owner of the count, which turns this from an
absence into a stated exception. `contracts/telemetry.md` records it as a known gap, and
`IRequestTelemetryFeature`'s doc comment warns the one owner who would otherwise try.

## The options, neither of which is cheap

1. **Move the method allowlist behind stage 2.** Then it writes the feature like every other stage.
   It changes the stage order in `contracts/pipeline.md`, which is frozen at G1a, and it means a
   `TRACE` is logged and traced before it is refused — which is most of what putting it first bought.
2. **Give `MethodAllowlist` its own `security.rejections` counter.** It contradicts "one owner counts
   this metric, so the count equals the request lines carrying a rule", which is the reason stage 2
   counts it at all, and it exports a second metric stream under a second scope.
3. **Narrow the contract.** Say in `contracts/request-gate.md` that a stage-1 rejection is logged
   under its rule id and not counted, and why. Cheapest, and it makes the gap a decision rather than
   an omission — but it weakens a sentence workstream 6's negative suite reads.
4. **Count it without the request line.** Stage 1 is inside the telemetry project's reach only if the
   allowlist moves there, which is an ownership change.

## Acceptance
- [ ] One option chosen and recorded, with its reasoning
- [ ] If the stage order changes, `contracts/pipeline.md` and `src/Gateway/Program.cs` change together
      and every workstream that built against the order is named
- [ ] If the contract is narrowed, `contracts/request-gate.md` says so and
      `contracts/telemetry.md`'s "known gap" paragraph is replaced by the decided rule
- [ ] Whatever is chosen, `contracts/telemetry.md` stops calling it a gap waiting for a gate

## Why this status
It needs an integrator decision, and every option is a gate-level change under the rule change
request 6.4 just wrote into `contracts/README.md`: option 1 changes a stage, options 2 and 3 change a
frozen contract's rule. None of it blocks anything — tickets 50, 51 and 52 are all independent of it,
and the gap is recorded in two places meanwhile.

Suggested for G1b, alongside I-6, because that is the next gate at which a frozen contract may move.

## Comments

Filed by the integrator on 2026-10-14. Found by the `code-review` pass on the change request 6.4
decision, which checked the writer table by running the pipeline rather than by reading it, and
caught that the table and overnight ticket 46's Q78 both listed task 4.6 as a writer. It is not one.
