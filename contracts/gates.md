# Gate register

The gates defined after the 11 October 2026 export, each with what it proves, its exit test, what it
unblocks and the decision that recorded it, and beneath it the test ids its exit test names, with
the tiers each must pass in, and the acceptance criteria those ids prove.

The export's own gates, G1a to G5, are the gates table of
[`docs/plan/01-plan.md`](../docs/plan/01-plan.md) and are not copied here. A gate both define is
refused by `.claude/workflows/gate-evidence.js`, since which exit test closes it would be a guess.

## Why this file is here

Decided on #364 (Q1, confirmed in #384). The export is frozen, and its gates table stops at G5:
"Phase 2 adds G6 for multi-task continuity, G7 for PingFederate and G8 for enterprise readiness",
and the Phase 2 tab gives each package an "Accepted when" sentence and no test ids. A gate with no
exit test cannot be closed, so a gate defined after the export needs a live home that the
gate-evidence route reads. A gate is not a test id, so the test id register,
[`test-ids.md`](test-ids.md), does not hold it, and an exit test changes as ids are numbered or
retired, which an ADR does not suit. This file never freezes.

**How a gate is added.** The integrator adds or changes a gate's row by landing a decided change
request (`docs/agents/tracker-policy.md` → "Change requests"), never the workstream that needs it,
as a test id register row is added. The register names ids that the export or the test id register
defines, and defines none: an id a gate needs is numbered there first. No exit test here may name a
retired test id.

**Row shape** (#364 Q6). Each gate takes a section of its own holding three tables, and no other
table, because the gate-evidence route reads every table here and refuses a gate it cannot read:

1. a gates table of one row, Gate | Proves | Exit test | Unblocks | Recorded by, whose Exit test
   cell is the one sentence the gate's tag message quotes;
2. beneath it, Id | Tiers required: one test id a row, never a range, its tiers a list of `H`, `L`
   and `D`, such as `H`, `H, D` or `L, D`, so the ids are read as data and not parsed from the
   sentence's prose; a `B-` id takes only `L` and `D` (ADR-0033, "The proof exception");
3. beneath that, where the gate names acceptance criteria, Criterion | Ids: each criterion with the
   ids of the table above that prove it, `—` for none (#364 Q7).

## G6 Multi-task continuity

The gate of roadmap stage S8, closed by the tag `g6-continuity` after #348's sitting. Its ids are
#538's hermetic two-host rows, #539's deployed rows for #348's rows 3 to 6, 8 and 9, F-07 deployed
for row 7, and P-01 hermetic and P-05 deployed for row 10 (#364 Q2). #348's rows 1 and 2, the
configuration reads and the cutover's sign-out record, are not test ids: the tag message cites
their comments on #348 (#348 Q34).

| Gate | Proves | Exit test | Unblocks | Recorded by |
| --- | --- | --- | --- | --- |
| G6 | Multi-task continuity: sessions, data-protection keys, refresh, revocation and the registry hold across two or more gateway tasks and through their replacement (P2-1 to P2-5) | F-12 to F-18 and N-28 to N-31, hermetic. F-19 to F-23, N-32 and F-07, deployed. P-01, hermetic, and P-05, deployed. The seven acceptance sentences of P2-1 to P2-5 signed off against the ids mapped to them. | G7, next on the ladder: the PingFederate profile (P2-6) | #364 Q1, Q2, Q6 and Q7, confirmed in #384; #404 Q3 to Q6, confirmed in #442; the ids numbered by #538 and #539 |

| Id | Tiers required |
| --- | --- |
| F-12 | H |
| F-13 | H |
| F-14 | H |
| F-15 | H |
| F-16 | H |
| F-17 | H |
| F-18 | H |
| N-28 | H |
| N-29 | H |
| N-30 | H |
| N-31 | H |
| P-01 | H |
| F-19 | D |
| F-20 | D |
| F-21 | D |
| F-22 | D |
| F-23 | D |
| N-32 | D |
| F-07 | D |
| P-05 | D |

| Criterion | Ids |
| --- | --- |
| P2-1 The store contract suite passes. | — |
| P2-1 Killing a task loses no session. | F-13, F-20 |
| P2-2 A cookie and a login state issued by one task are accepted by another. | F-13, F-14, F-15, N-28, F-19, F-21 |
| P2-3 Fifty parallel calls split across two tasks cause one refresh grant and no refused grant. | F-23 |
| P2-3 A session ended through one task is refused by the other on its next request. | N-32 |
| P2-4 A rolling deployment and a killed task, with live sessions, cause no sign-out and no failed login. | F-20, F-22 |
| P2-5 A login that starts on one task and finishes on another succeeds during a reload. | F-16, F-17, F-07 |

Each criterion is one sentence of the package's "Accepted when" cell in
[`docs/plan/06-phase-2-and-risks.md`](../docs/plan/06-phase-2-and-risks.md), quoted whole. What the
mapping shows, so a reader signing them off does not have to work it out:

- **P2-1's first sentence has no id.** The store contract suite is proved against the durable store
  under its plan row's name in workstream 3's project, and no register row numbers it.
- **P2-3's first sentence has only its deployed half.** F-23 claims one Refreshed line for the
  session and no grant count (#348 Q30); the grant count is the hermetic provider counter's, proved
  under its plan rows' names and named by no id.
- **Seven ids prove a G6 behaviour no sentence states.** F-12 is the two-host harness the other
  hermetic rows run on; F-18 is the session store failing with health still answering; N-29, N-30
  and N-31 are a rotation across hosts, the "rotation" half of P2-5's change: the purge reaching
  the shared store, the purge failing, and a login in flight refused while a fresh one succeeds;
  P-01 is the platform plan's allowlist, its DynamoDB tripwire conditional on the session store
  (#338); and P-05 is the teardown that ends the sitting.

## G7 and G8

Not defined yet. Each takes a section here, in the shape above, when the decision on its exit test
lands: G7's owed ids are in the test id register's "Owed" table. Until then the gate-evidence route
refuses each as having no exit test (#364 Q3).
