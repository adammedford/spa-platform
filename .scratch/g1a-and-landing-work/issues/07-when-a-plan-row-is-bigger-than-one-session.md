# When a plan row is bigger than one session

Type: grilling
Status: resolved

## Question

Graduated from the map's fog by ticket 04, which settled the two things this depended on: the branch
is the claim, and the landing unit is one row per branch and PR.

The plan sized its rows for one agent each. At least one is not. **Task 2.1** measured:
`docs/plan/02-contracts.md:52-64` lists 11 rule bullets expanding to ~20 named rules, and
`contracts/README.md` adds a table of 13 rules JSON Schema cannot express, 11 of them 2.1's, plus
errors-vs-warnings separation and canonical-JSON fingerprinting (S-D8, N-14). Each rule needs a
passing and a failing case, so the row is of the order of 40 to 60 tests.

Ticket 04 already supplies most of the mechanism: the branch outlives the session, so a second
session resumes `task/2.1-*` rather than claiming anything new. What is left open:

1. **Does a split row get sub-ids?** `docs/plan/` is a **frozen export** and cannot gain rows, so
   `2.1a`/`2.1b` would exist nowhere authoritative. The alternatives are one branch carrying several
   sessions' work, or `.scratch/` tickets under the row, which is the shape `/implement-spec` wants
   and which ticket 04 made opt-in.
2. **What the merge subject says** when one PR lands part of a row. Ticket 04's decision 6 has the
   subject name every task id the PR lands; a half-row has no id of its own.
3. **How "Done when" is judged** on a partial landing. The row's test ids are the whole row's.
   Ticket 04's decision 5 makes a dependent row's dispatch turn on "a merge naming that id is in
   `main`", so a partially-landed 2.1 would unblock 2.2 and 2.3 falsely.

Item 3 is the sharp edge: the `Needs` check ticket 04 settled is **wrong** in the presence of a
partial landing, and 2.1 is the row four others depend on (2.2, 2.3, 2.6 directly).

## Answer

Nine decisions. Five of the ticket's premises were wrong, and two of them in the direction that
makes the question easier rather than harder.

### Five premises corrected

**"At least one row is not [sized for one agent]" understates it.** Task 2.1 is not *completable* at
G1a at any size. Its "Done when" is "every rule in the Contracts tab has a passing and a failing
case"; `registry.schema.json#/$defs/oauth` is draft until G1b (`contracts/README.md` -> "What is
draft until G1b"); and `docs/plan/03-workstreams-and-tasks.md:104` says "2.1 and 2.6 absorb whatever
G0 changes in it". So partial landing was never a thing to permit or forbid — the frozen plan
already requires one, for a reason that has nothing to do with session size. The ticket framed this
as a capacity problem; it is two problems that share one mechanism.

**Item 3's sharp edge is blunt for 2.1 itself.** "A partially-landed 2.1 would unblock 2.2 and 2.3
falsely" is false in this case: 2.1's test ids are N-14 and S-D8, both the duplicate-host,
disallowed-upstream and forbidden-CSP rules (`docs/plan/04-tests.md:53`,
`docs/plan/07-evidence.md:73`) — no oauth among them — and neither 2.2 nor 2.3 touches the oauth
rules. A 2.1 that lands without them unblocks its dependents *legitimately*, with every test id it
owns proven. The general risk survives only where the missing part is one a dependent needs, which is
why decision 7 turns it into a stated judgement rather than a prohibition.

**The two-state model is already in `main`, unnamed.** `Merge tasks 1.0 and 1.6a: written and
statically validated, not applied` names both ids, unblocks their dependents, and says in its body
"Neither task's 'Done when' is met: both need AWS credentials, and 1.6a needs the workstation trust
store." The change request open against 1.6a was a genuine integrator-path ask, *separate* from the
done-when gap — so a change request is not the record of outstanding own-work, and `GLOSSARY.md`'s
narrow definition of the term stays intact. These decisions formalise what the repository already
does.

**No gate exit test consumes "done".** All seven at `docs/plan/01-plan.md:230-238` are expressed in
test ids and behaviours, never in task completion. So "done" has no mechanical consumer and never
needed one; the only thing a partial landing can endanger is an unproven **test id**, which is what
decision 6 records.

**Row length predicts nothing.** By description-plus-done-when text, 2.1 is 14th of 54; by test count
it is the largest, because its work lives in a table the row only points at (the 11 rule bullets at
`docs/plan/02-contracts.md:52-64` plus `contracts/README.md`'s 13 rules). There is no size signal
available at dispatch, so every mechanism here has to be discoverable mid-flight by a session that
has already started.

### The decisions

**1. Landed and done are two states, and the `Needs` check asks for the weaker one.** A **merge
naming the id** means *landed*: everything a dependent row needs is in `main`, and anything absent is
declared. **Done** means the row's "Done when" holds with nothing outstanding. Ticket 04's decision 5
("a task id means a merge naming that id is in `main`") is therefore correct as written, because
dependents only ever needed *landed*. This dissolves item 3 without a new mechanism.

**2. No sub-ids, ever.** `docs/plan/` is a frozen export, so `2.1a` would exist in no authoritative
place and would be the second copy of the plan this project keeps refusing. A row that takes three
sessions stays one task, one id, one claim; what varies is how many merges carry that id.

**3. Two reserved subject prefixes.** `Merge task(s) <id...>: <gist>` means landed. `Merge part of
task <id>: <gist>` means a partial that is **not** yet landed. The id stays in both, so `git log`
remains the record `AGENTS.md` sends people to, and the *prefix* carries the dispatch signal. Ticket
04's decision 5 check stays one command: `git log --first-parent --format=%s | grep '^Merge task'`.

**4. The branch's tests are the resume record; no progress document.** A resuming session derives what
remains by diffing the rule list in `contracts/README.md` and `docs/plan/02-contracts.md:52-64`
against the test names already on the branch — which is exactly what 2.1's "Done when" asserts, so
**checking the row and resuming it are the same act**. Rejected: a `.scratch/` ticket set as the
record (gitignored, so it does not survive a fresh clone, and ticket 04 already rejected it as a
claim surface) and a hand-maintained note in the PR body as the authoritative record.

**5. Partial merges are the normal way a big row lands, and the claim branch outlives them.** Session
1 merges what it has under the `Merge part of task` prefix rather than hoarding it for session 3: a
50-test PR is not reviewable by the separate session the map requires, CI holds the work from the
first session instead of the third, and decision 3 keeps the `Needs` check correct while it sits
there. The branch is deleted when the row **lands**, not when a PR merges, so ticket 04's "the branch
is the claim" holds for the row's whole life and git keeps refusing a double claim. GitHub permits a
second PR from a branch whose first PR has merged.

**6. An `Outstanding:` trailer on any merge that names an id without the row being done.** Prose
cannot answer "what does WS2 still owe?"; a trailer can, in one command, and it matches the shape
A-05's integrator declaration already uses:

```
Outstanding: 2.1 — the oauth rules' passing and failing cases. Blocked by G1b.
Outstanding test ids: none.
```

Because no gate consumes "done", this trailer has **no mechanical consumer**: it exists so a row
cannot be quietly forgotten, and that is precisely why it stays one line and must not grow into a
report. `Outstanding test ids` is the half that *does* have a consumer, since a gate's exit test is a
list of ids. A row is done when the last merge naming it carries no such trailer.

**7. The merge body names the dependents the landing claims to satisfy.** Decision 1 makes "landed" a
session judgement, and a judgement with no stated object cannot be reviewed. So a merge that names an
id states which rows it unblocks — for 2.1, "landed for 2.2, 2.3 and 2.6". The reviewing session's
`/code-review` spec axis checks it against those rows, which needs no new review machinery because
ticket 04's decision 3 already makes the row plus its contracts rows the spec.

**8. `/implement-spec` is the recommended tool for an oversized row, with two corrections to ticket
04.** It works on an **integration branch**, with implementer subagents each on their own branch
merging into it, and lands one PR — which is the right shape here, with the integration branch being
the claim branch `task/2.1-<slug>`.

- **Implementer branches are `impl/<task-id>-<slug>`, not `task/*`.** Ticket 04's decision 4 says they
  "still follow decision 1's naming so the claim holds"; that cannot work. Git refuses a second
  worktree on the claim branch, so the implementer branches need distinct names, and if they are
  `task/*` then `git branch --list 'task/*'` reports five claims for one task and the enumeration
  stops meaning anything. `impl/*` keeps `task/*` at exactly one branch per claimed task and makes
  the implementer branches visibly subordinate and disposable.
- **Its step-7 `code-review` does not discharge the map's separate-session review.** Step 7 runs the
  review from the orchestrating session; the map requires a session other than the one that wrote the
  code. Ticket 04 flagged that as the weakest reading, and for an oversized row this becomes the
  normal path rather than an edge case, so the PR still gets a review from another session.

Its `.scratch/` tickets are then a **session-local work plan** — gitignored, disposable, dying with
the effort — which is safe exactly because decision 4 makes the tests the durable record and decision
2 keeps sub-ids out of the plan. They are not a second claim surface and not a second copy of the row.

**9. No cap and no escalation when a row proves badly sized.** A row needing six sessions triggers
nothing automatic. The `Outstanding:` trailers are already the record of what is owed, and whether the
plan mis-sized its rows is a question for a gate retrospective, not a per-row threshold. Rejected: a
session count that forces a plan amendment (the plan is a frozen export and cannot gain rows, which is
what decision 2 turns on) and `docs/gaps.md` (WS6's path, absent, and a mis-sized row is a defect in
the plan rather than a gap in the system).

### What this puts into `docs/agents/landing-work.md`

The two states and which one `Needs` asks for; the two reserved subject prefixes and the one-command
check; the `Outstanding:` trailer and its two fields; the dependents line; the branch's lifetime
(deleted on landing, not on merging); the resume-by-diffing-tests rule; and the `/implement-spec`
recipe for an oversized row.

Plus **one worked example, and it is 2.1**, because it exercises every part at once: oversized, forced
partial by a draft contract, and the row four others depend on. The example cites the **`x-freeze`
annotation** on `$defs/oauth` as its source rather than asserting on its own authority that 2.1 cannot
complete at G1a — `Contracts.Tests` and `FreezeMarkersTests` (357a475) already fail if that annotation
and its code form disagree, so the one fact the example caches cannot drift silently past CI. That
mitigation is the whole reason a worked example is allowed here at all: a cached judgement about a
frozen plan is otherwise exactly the failure mode this project keeps designing against.

### Handoff notes beyond `landing-work.md`

- **Read the `Outstanding:` trailer from the merge commit's own message, first-parent, not from the
  commit range.** `.github/path-ownership.py`'s `declaration()` greps the whole `merge-base..head`
  range, which is the over-wide matching ticket 08 is open on. A range-wide `Outstanding:` grep would
  repeat the defect, letting one session's trailer answer for a later one's merge.
- **Ticket 04's decision 4 is corrected** by decision 8's first bullet. Whatever writes
  `landing-work.md` takes the `impl/*` form, not the `task/*` form that ticket's text implies.
- **The glossary pass gains two entries.** **Landed** and **done** are now load-bearing vocabulary
  with a deliberate gap between them, and `GLOSSARY.md` defines neither — nor **session** or
  **claim**, which ticket 04 already queued, in the same "Work, gates and evidence" section whose
  "one of the six parallel **lanes**" defect ticket 04 also flagged. One pass fixes all of it. Say
  **task** for the work and **row** only for the line in the document.
- **The map's Settled "Landing unit" row is refined, not reversed**: one branch per task still holds,
  but one branch can carry several merges and so several PRs.
- **The merge body now has four or five required parts** (verification note, dependents line,
  `Outstanding:` trailer, any open change request, and whatever ticket 08 settles for A-05). Whether
  that shape is asserted or left to convention is ticket 11.
