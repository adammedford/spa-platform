# Whether the merge body's required parts are asserted

Type: grilling
Status: closed
Blocked by: 08

## Question

Surfaced by ticket 07. The merge commit has quietly become a form, and nothing checks it.

Its parts, each decided by a different ticket:

| Part | Decided by | Shape |
| --- | --- | --- |
| Subject prefix and every task id | 04 decision 6, 07 decision 3 | `Merge task(s) <id...>:` or `Merge part of task <id>:` |
| The verification note | map, Settled -> Landing unit | prose |
| The dependents this landing claims to satisfy | 07 decision 7 | prose, one line |
| `Outstanding:` and `Outstanding test ids:` | 07 decision 6 | trailers, on any merge naming an id where the row is not done |
| Any open change request | map, Settled -> Change request | prose |
| The A-05 integrator declaration | ticket 08 | trailer, shape to be settled |

Ticket 04 decision 5 makes the subject a **dispatch precondition** — a dependent row's `Needs` is met
when a merge naming that id is in `main` — and ticket 07 decision 1 makes the `Outstanding:` trailer
the only record that a landed row is not done. Both are load-bearing, and both are prose conventions
a tired session can simply not write.

The repository's own precedent cuts one way. 357a475 replaced two prose rules with checks after a
paragraph had already failed in the file every agent loads first: "a copy of the state goes stale and
then misleads every session that loads it" survived a full session sitting directly above the line it
banned. `.github/steering.py` exists because of it, it runs in CI and in `.githooks/pre-push`, and
ticket 03's guard lands in the same two places.

Against that: the hook and CI see a push, not a merge commit, so a merge-body check has an awkward
trigger; a required-parts check that fires on the integrator's own merges is one more thing to fight
at handoff; and ticket 07 decision 6 deliberately left the `Outstanding:` trailer with **no**
mechanical consumer, on the grounds that its job is to stop a row being forgotten and it must stay
cheap.

So: which parts, if any, are asserted, where does the assertion run, and what does it do about the
merges already in `main` that predate the convention?

Note from ticket 07: whatever reads a trailer must read the **merge commit's own message**, first
parent, not the `merge-base..head` range. `.github/path-ownership.py`'s `declaration()` greps the
range, which is the over-wide matching ticket 08 is open on.

## Answer

<!-- which parts are asserted, where, and what happens to the existing history -->

## Closed: out of scope (the cut)

`Status: resolved` is wrong for this and it is not resolved; it is **closed unresolved**, because
the thing it decides about no longer exists. Commit 3d344fd cut the coordination apparatus down to
what one author needs: the repository had 6,437 lines of process against 611 lines of product code,
and none of the platform worked yet.

What that removed, which this ticket depended on:
- The A-05 declaration is deleted, so the merge body's one machine-read part is gone.
- Nothing else in the body is asserted, and that is now the decision rather than a deferral. The
  subject prefixes, the verification note and the `Outstanding:` lines stay conventions in
  `docs/agents/landing-work.md`, carried by one author who writes them or does not.
- The question of what to do about merges predating the convention dies with it. 33eb5fb stands.
