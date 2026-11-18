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

## G9 Fragments

The gate of roadmap stage S8a, closed by the tag `g9-fragments` after its own deployed sitting
(#961's T11), which runs after G6's (#348) because it needs durable sessions and two tasks. G7 does
not wait on it: S8a before S9 is ladder order only, and whichever of G7 and G9 closes second carries
the per-client key question (#776). Its ids are the functional core and obligations O3, O5 to O8 and
O10 to O13 of the fragments spec (#961), numbered by #989.

| Gate | Proves | Exit test | Unblocks | Recorded by |
| --- | --- | --- | --- | --- |
| G9 | Composition: one host serves one shell and its fragments with N + 1 OAuth clients at one issuer, one sign-in and one sign-out, each fragment's API seeing only its own client's token, and each fragment's release activated by its owner with no restart and the shell's release untouched (S8a) | F-24 to F-30, F-32 to F-36, F-38, N-36, N-37 and N-39 to N-45, hermetic. F-31, F-37, F-39 to F-42, N-38 and N-46, deployed. The three acceptance sentences of #961 signed off against the ids mapped to them. O1, O2, O4 and O9 in the sitting record, O1's count replacing the fragments-per-shell placeholder and O2's figures setting `MergeBudget`. | Nothing waits on it: S9 follows on the ladder, and fragments under PingFederate is decided at S9 | #776; #961, accepted in https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645455906; the ids numbered by #989 |

| Id | Tiers required |
| --- | --- |
| F-24 | H |
| F-25 | H |
| F-26 | H |
| F-27 | H |
| F-28 | H |
| F-29 | H |
| F-30 | H |
| F-32 | H |
| F-33 | H |
| F-34 | H |
| F-35 | H |
| F-36 | H |
| F-38 | H |
| N-36 | H |
| N-37 | H |
| N-39 | H |
| N-40 | H |
| N-41 | H |
| N-42 | H |
| N-43 | H |
| N-44 | H |
| N-45 | H |
| F-31 | D |
| F-37 | D |
| F-39 | D |
| F-40 | D |
| F-41 | D |
| F-42 | D |
| N-38 | D |
| N-46 | D |

| Criterion | Ids |
| --- | --- |
| (1) One login gives the shell and every fragment a token set with no second interactive sign-in inside the provider session. | F-24, F-33, F-34, F-40, F-42 |
| (2) Each fragment's API sees only its own client's token. | F-38, N-37 |
| (3) A fragment's release activates with no restart and leaves the shell's release alone. | F-29 |

Each criterion is one of the three acceptance sentences of #961 → "The stage and gate", quoted
whole. What the mapping shows, so a reader signing them off does not have to work it out:

- **Criterion (1)'s hermetic ids prove the chain, not the silence.** TestIdp has no provider
  session and ignores `prompt` unless workstream 5's fidelity switch (#997, #961's T7) is on, and
  it is off by default, so F-24 and F-33 prove one interactive sign-in and a completed chain; F-40
  and F-42 prove the silence on Cognito.
- **Criterion (3) is F-06's shape for a fragment.** F-29 asserts A → B → A and also carries O6's
  two document-level cases, the first import after activation and the missing lazy chunk.
- **Twenty-two ids prove a G9 behaviour no sentence states:** an unavailable fragment (F-25), logout
  (F-26, N-38), `GET /bff/fragments` (F-27), the policy (F-28, N-36, F-39), `schemaVersion`
  (F-30, F-31), the refresh regression (F-32), the concurrency traces (F-35), the asset invariant
  (F-36, F-37), the entry import (F-41), the reserved paths and publish (N-39 to N-41), the
  import-map write check (N-42), and the chain step's refusals (N-43 to N-46).

**The obligations, and how each is proved.** O1 to O13 are #961's Testing Decisions; the four with
no id are sitting records, cited by the tag message as #348's comments are for G6 rows 1 and 2.

- **O1, Cognito redirects and the cap:** a sitting record of redirects from `/bff/login` to
  `returnUrl` at N = 0 to 6 and per hop. G9 does not close until its count replaces the
  fragments-per-shell placeholder of 5.
- **O2, refresh latency and write conflicts:** a sitting record of the p95 refresh at 1 + 5 due sets
  and the conflicts and attempts per write, on one task and two, with run ids. G9 does not close
  until it confirms or changes the per-set gates and sets `MergeBudget`, a defect at its placeholder
  of 3 until then.
- **O3, successor revocation across tasks:** N-38. A counterexample blocks G9 until a fix, or
  until Adam's decision comment on #961 accepts the residual and `docs/gaps.md` gains its entry.
- **O4, record size and write cost:** a sitting record of the serialised bytes and write units at
  1 + 5 sets.
- **O5, import-map browser conformance:** F-39.
- **O6, activation behaviour:** F-29.
- **O7, concurrency traces:** F-35.
- **O8, the prompt-less fallback on one host:** F-40.
- **O9, header limits:** a sitting record of the ALB, Kestrel and browser limits against the merged
  policy at the cap, and the merged size.
- **O10, the cache invariant:** F-36, and F-37 on the cell's bucket.
- **O11, cross-client selection and refusal:** F-38 and N-37.
- **O12, the entry import:** F-41.
- **O13, chain navigation on a real provider:** F-42 and N-46.

The tag message quotes the exit test and cites the sitting records for O1, O2, O4 and O9.
