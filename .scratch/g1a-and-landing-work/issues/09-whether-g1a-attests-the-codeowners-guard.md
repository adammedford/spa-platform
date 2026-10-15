# Whether G1a attests the CODEOWNERS guard

Type: grilling
Status: closed
Blocked by: 03, 05

## Question

Ticket 03 decided the root `CODEOWNERS` keeps 26 deliberate `Unknown owner` errors, and found that a
later `.github/CODEOWNERS` would **silently** shadow the ownership record while **A-05 kept
passing** — no diagnostic on either side. Its guard: fail if `.github/CODEOWNERS` exists, in both
`.githooks/pre-push` and CI.

Ticket 05 set G1a's six items, of which item 5 already covers A-05. So the candidate is **one more
item**, or a clause on item 5:

> the record A-05 reads is the root `CODEOWNERS`, and nothing shadows it

evidenced by the new CI guard. ADR-0014 is a decision record, so it is **cited, not attested**.

What makes this worth its own ticket rather than a line in either: **the guard reintroduces the
question ticket 05's item 3 declared moot.** Half of it lives in `.githooks/pre-push`, which is
per-clone opt-in (`git config core.hooksPath .githooks`, and ticket 04 confirmed one per clone). A
checklist item leaning on the hook is therefore **convention-carried**, not enforced — exactly the
wording problem ticket 06 anticipated and ticket 01 was thought to have removed by making the
repository public. The CI half *is* enforced, since all three checks are required on `main`.

So:

1. Is it a sixth-and-seventh item, a clause on item 5, or nothing the gate attests?
2. If the checklist cites the hook at all, how is a convention-carried item worded, given
   `docs/gates/README.md` is being written as the template for seven gates?
3. Does the CI half alone suffice, making the hook a convenience the checklist never mentions?

## Answer

<!-- whether it is an item, and how a convention-carried item is worded -->

## Closed: out of scope (the cut)

`Status: resolved` is wrong for this and it is not resolved; it is **closed unresolved**, because
the thing it decides about no longer exists. Commit 3d344fd cut the coordination apparatus down to
what one author needs: the repository had 6,437 lines of process against 611 lines of product code,
and none of the platform worked yet.

What that removed, which this ticket depended on:
- `CODEOWNERS` is now `OWNERSHIP.md`, a plain file of role names. There are no handles, so no 26
  errors, so nothing for a `.github/CODEOWNERS` to shadow, so no guard — and nothing to attest.
- The gate checklist convention is dropped. A gate closes when its exit test in
  `docs/plan/01-plan.md` passes, and the annotated tag names the CI run that proves it. There is no
  `docs/gates/`, no five evidence fields, and no attestation line, so the question of how a
  convention-carried item is worded has no subject.
- ADR-0014 is not written. The decision it would record — keep 26 deliberate errors — was reversed.
