# ADR-0015: A value two workstreams must agree on lives in `Platform.Abstractions`; a measured value stays where it is measured

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** integrator (task I-7)
- **Plan ref:** No decision row. A-03 in `docs/plan/04-tests.md` says a workstream project
  references `Platform.Abstractions` and no other workstream's project, which is what forces the
  question. Raised from inside two tasks on 14 October 2026 — 2.3a and 4.1a — neither of which
  filed a change request, because neither was blocked.

## Context

Two tasks hit the same wall on the same day, from opposite directions.

**Task 2.3a** needed the rule id strings that `contracts/request-gate.md` lists. `Platform.Security`
already had constants for its own; A-03 puts them out of reach of `Platform.Hosting`, so 2.3a added
`src/Platform.Hosting/RuleIds.cs` — a third copy, after `Platform.Security`'s and the private
`Rule` consts in `Platform.Proxy`. Workstream 3 will add `auth.callback` and `auth.session` after
G1b, and workstream 6's negative suite asserts on all twelve, so the copy count was heading for
five. Each copy agrees with the contract only because someone read the contract carefully, and
`contracts/request-gate.md` froze at G1a, so the list is not going to move and the copies are pure
risk.

**Task 4.1a** went the other way. It measured the largest `apis[].timeoutSeconds` YARP actually
applies — 2,147,483 s, above which `ActivityCancellationTokenSource` saturates at `int.MaxValue`
milliseconds and the proxy silently applies a limit other than the one the registry states. Ticket
49 wants the registry validator to refuse anything above that, and the validator lives in
`Platform.Registry`, which A-03 stops from referencing `Platform.Proxy`.

The two look alike — "a constant two workstreams need" — and they are not alike at all. One is a
value the **contract** fixes and the code must match. The other is a property of a **pinned
dependency**, true of YARP 2.3.0 on .NET 10.0.12 and of nothing else.

## Decision

**1. A value the contracts fix, which more than one workstream must spell identically, is a public
constant in `Platform.Abstractions`, and a test asserts it against the contract.**

`RuleIds` holds the twelve ids from `contracts/request-gate.md` → Rule ids.
`tests/Platform.Abstractions.Tests/RuleIdsTests.cs` parses that table out of the contract and fails
if the constants and the table disagree, in either direction or in order. The contract stays the
source of truth; the constants stop being a transcription nobody checks.

This is an addition to a project that freezes at G1a, which `PublicSurfaceTests` deliberately makes
noisy: the type is added to the contracted surface list in the same commit. It is not marked
`[Freeze("G1b")]`, because `contracts/request-gate.md` froze at G1a and the two identity ids are
already in its table — G1b changes who writes them, not what they are.

**2. A value measured from a pinned dependency stays in the project that owns that dependency.**

`ProxyTimeout.MaximumTimeoutSeconds` stays in `Platform.Proxy`. `Platform.Abstractions` must not
carry a number whose truth depends on a YARP version, and it must not acquire a reason to care
which proxy the platform uses.

**3. When the registry needs a bound the proxy can honour, the contract states the bound and the
proxy proves it — not the reverse.** `contracts/registry.schema.json` gives `timeoutSeconds` a
`maximum`, `Platform.Registry`'s validator refuses anything above it, and `Platform.Proxy` keeps the
measuring test from task 4.1a, now asserting that the dependency can apply the contracted bound. A
YARP upgrade that lowers the ceiling then fails in the project that owns YARP, which is where
someone can act on it.

**4. Decision 3 is deferred, with its condition written down.** `registry.schema.json:162` says of
`timeoutSeconds` that "the plan states no upper bound", so adding a `maximum` changes a frozen
schema and raises the `schemaVersion` question that gap MR-1 in `docs/roadmap.md` says nothing yet
answers: no rule states which registry documents a given gateway build accepts. The bound lands
with that rule, not before it. Until then ticket 49 stays open and the validator keeps its present
model-level bound, which is wrong by a factor of about 430,000 but wrong in the safe direction —
it refuses nothing the proxy would have applied correctly.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Leave each workstream its own copy of the rule ids | What we had. Five copies of a frozen twelve-row table, agreeing only by care, with the negative suite — the one component whose job is to be adversarial — depending on all five being right. |
| Put the ids in `Platform.Testing` instead, which every workstream already references | `Platform.Testing` is doubles for tests. A production refusal would then name a constant that lives in a test-support project, and a workstream would reference test code from `src/`. |
| Generate `RuleIds.cs` from the contract at build time | Removes the drift by construction, and costs a generator, a build step and a file nobody can read in a review. A test that fails loudly buys the same guarantee here, because the list is frozen and twelve rows long. |
| Mirror `MaximumTimeoutSeconds` into `Platform.Abstractions` so ticket 49 can read it | Puts a YARP-measured, version-specific number into the project with no dependencies, and inverts the direction of proof: the registry would be bounded by whatever the current proxy happens to do, rather than the proxy being held to what the contract promises. |
| Relax A-03 so `Platform.Registry` may reference `Platform.Proxy` | A-03 is what keeps the workstreams independently buildable and reviewable. Trading it for one constant is a bad price, and the next shared constant would re-raise it. |

## Consequences

- Three adoption tickets follow, one per owner, each deleting a copy and depending on nothing else:
  workstream 2 (`src/Platform.Hosting/RuleIds.cs`), workstream 4 (`Platform.Security`'s constants and
  `Platform.Proxy`'s private `Rule`), and workstream 3 at G1b (write `auth.callback` and
  `auth.session` from `RuleIds` rather than from new literals).
- They are **adoption, not blocking**. PRs #22 and #23 land their own literals first and are
  correct as written; the tickets swap the literals afterwards. Nothing is held up waiting for this.
- Workstream 6's negative suite reads `RuleIds.All` instead of a thirteenth copy, which also makes
  "every rule id has a refusal test" expressible as a test rather than as a reviewer's diligence.
- `Platform.Abstractions` gains its first constant that is neither a type nor an interface. The
  public-surface test makes the next such addition a visible decision too.
- Ticket 49 (2.1b) is blocked on decision 4 rather than on task 4.1a. That should be recorded on the
  ticket, because its stated blocker is now satisfied and its real one is not.

## Evidence

- `tests/Platform.Abstractions.Tests/RuleIdsTests.cs`: the constants match `contracts/request-gate.md`
  in content and order; every public constant is in `All`; no id appears twice; each id is a
  lower-case single-dotted token, the shape `security.rejections` assumes when tagged by rule.
- `tests/Platform.Abstractions.Tests/PublicSurfaceTests.cs`: `RuleIds` is on the contracted surface
  and is not marked draft.
- Task 4.1a's `ProxyTimeoutCeilingTests` already bisects the real ceiling out of YARP, so decision 3
  needs no new measurement when its condition is met — only the schema's `maximum` and one changed
  assertion.
