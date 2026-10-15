# How a workstream session takes a task

Type: grilling
Status: resolved

## Question

Six workstreams run concurrently as agent sessions. Nothing stops two of them taking the same task.

The plan's dispatch table says what *opens* when, but it is a frozen export with no state: it cannot
record that task 2.1 is in progress. The `.scratch/` tracker has a claim mechanism, but it is for
wayfinder tickets, not for plan tasks. So right now a task is claimed by a human remembering which
session they started.

What has to be settled:

1. **What a session is handed.** A task id and a pointer to its plan row? A written brief? The row
   plus the contracts it has to satisfy? Each workstream task already has "What / Needs / Done when"
   in the plan, which may be enough on its own.

   One concrete item is already settled and has to appear here: ticket 06 chose a tracked pre-push
   hook wired by `git config core.hooksPath .githooks`, which is per-clone and per-worktree. So
   session setup has at least one mandatory step, and this ticket decides whether that is a checklist
   the session runs, something the dispatcher does when making the worktree, or a line in
   `AGENTS.md`.
2. **Where in-progress state lives**, if anywhere. Candidates: a branch whose name encodes the task
   (`task/2.1-registry-validator`), which makes `git branch` the claim list and costs nothing; a file
   under `.scratch/`, which is gitignored and so invisible to other clones; GitHub issues, which
   would be a second tracker beside `.scratch/`; or nothing, with the human holding it.
3. **Whether `Needs` is checked before dispatch.** Task 2.2 needs 2.1 and 2.5. Does the session
   verify its inputs landed, or does the dispatcher? A session that starts on an unmet `Needs` builds
   against doubles that are not there yet.
4. **How a workstream with several ready tasks is driven.** One session per task, or one session per
   workstream working its rows in order? This decides whether `/implement-spec` is the right tool,
   since it wants a whole spec's task graph in one run, and it is the question left unresolved
   earlier in the effort that produced this map.
5. **What happens when a task turns out to be bigger than one session.** The plan sized its rows for
   one agent each, but task 2.1 ("every rule in the contract, with a passing and a failing case") may
   not be.

Settle this before `docs/agents/landing-work.md` is written: it is the first section of it, and the
fog about converting plan rows into `/implement-spec` tickets hangs off the answer.

## Answer

Six decisions, and two of this ticket's premises corrected before them.

### Two premises were false

**"Each clone and each worktree needs `git config core.hooksPath .githooks`."** Wrong, and wrong in
three places: the map's Settled table, ticket 06, and `.githooks/pre-push:9`'s own header. The
setting lives in the shared `.git/config`, `extensions.worktreeConfig` is unset, and the relative
path resolves per worktree against the tracked hook. It is **one `git config` per clone, never per
worktree**, and the hook fires from a linked worktree with no extra config. The mandatory-setup list
for a worktree session was therefore *empty*, which deflates item 1 as written.

**"Nothing stops two of them taking the same task."** Wrong under per-task branches. Git already
refuses a second worktree on one branch (`fatal: 'wt' is already used by worktree at ...`) and
refuses `-b` on an existing name. The exclusion mechanism exists today, unused; what was missing was
only the convention that the branch is created at claim time and named for the id.

### The decisions

**1. The branch is the claim.** `git worktree add -b task/<lowercased-id>-<slug>`. Worktrees of one
clone share the ref store, so `git branch --list 'task/*'` enumerates every claim with no push and
`git worktree list` says which session holds which. Chosen because it is the only candidate that is
both free and *mechanically* exclusive — the exclusivity is git's, not a convention a session can
forget. Rejected: `.scratch/` (gitignored, and a second claim surface beside the one
`/implement-spec` already consumes); GitHub issues (a second tracker); nothing (one human's memory).
A **draft PR** was a fifth option the ticket omitted; it is the cross-clone fallback if a second
workstation ever appears, and the map already mandates a PR per task anyway.

**2. Session start is a short block of idempotent assertions the session runs itself**, in
`docs/agents/landing-work.md`, with one pointer line in `AGENTS.md` and none of the content. Session-
side because the orchestrator is a human *inside* one of these sessions: a step only the orchestrator
performs is a step nobody performs on the day they forget. Assertions rather than actions because the
corrected premise makes them no-ops in a worktree and real only in a fresh clone — task 1.6a's
"second workstation". The three worth asserting: `core.hooksPath` is `.githooks`; `user.email` is a
`users.noreply.github.com` address (the hook only *warns*, and ticket 01 had to rewrite history over
exactly this); and **every push pays a full `dotnet build`**, which also means `/implement-spec`'s N
implementer subagents pay N builds. A pointer rather than content in `AGENTS.md` because 91e2103
stripped cached state from it for precisely this reason.

**3. The hand-off is a fixed pointer set; nothing is restated.** Six pointers: the task id; its row in
`docs/plan/03-workstreams-and-tasks.md`; its owner paths from `CODEOWNERS`; the `contracts/README.md`
rows naming it under "Consumed by"; the `Platform.Testing` doubles it builds against; the test ids
under "Done when". `docs/plan/` is a frozen export, so any brief restating a row is a second copy
that drifts. But the row alone is incomplete three ways — it names no owner paths, no contracts, and
does not say which contracts are **draft** (`contracts/README.md` -> "What is draft until G1b", plus
the hard rule that nothing in `src/Platform.Identity` starts before `contracts-v1`, which does not
exist).

**4. One session per workstream working its ready rows, one branch and one PR per row.
`/implement-spec` stays opt-in.** Measured at the moment G1a closes: WS4 has **five** independent
ready rows (4.1, 4.3-4.6, no edges between them), WS2 two, WS5 three, WS6 two — "several ready rows"
is the normal case, and "in order" is arbitrary for WS4 while forced for WS3's five-deep chain. So
the real axis is not sequence but **whether parallelism lives in sessions or in subagents**.
`/implement-spec` is human-invoke-only and wants a `spec.md` plus per-ticket `Blocked by:` files that
do not exist; the dispatch table is already that graph, in a frozen export, and converting 54 rows
into a parallel tracker makes a second copy of the thing this project refuses to copy. Its step 7
also runs `code-review` from the session that orchestrated the work, which reads the map's "a
separate session" rule at its weakest. When it *is* used, its implementer branches still follow
decision 1's naming so the claim holds. `/to-tickets` exists if the conversion is ever wanted.

**5. The session verifies its own `Needs` before writing anything; the orchestrator's list is
advice.** The rule: **a gate name means the tag exists; a task id means a merge naming that id is in
`main`**; anything unmet stops the session and reports rather than proceeding. Measured: 15 of 54 rows
gate on a bare gate name, and `git tag` is **empty**, so all 15 are formally unmet today; 9 rows name
another workstream's task, and 1.7 alone names four across WS2 and WS5. Session-side because it costs
two commands and is the one check that guards against the orchestrator's own haste, which the map
already names as this model's weakest link. Worded as "the tag exists" rather than "the checklist
says so" deliberately, so it survives whatever shape ticket 05 gives `docs/gates/` and four
workstreams are not blocked on a document's wording.

**6. The merge subject names every task id the PR lands**, as `Merge task(s) <id...>: <gist>`, with
the verification note and any open change request in the body as the existing merges already do. The
defect is narrower than it first looked: the ids are **not** missing from `git log --oneline` —
`9399f75 I-5`, `6065d99 I-4`, `5399494 I-3`, `fa9ed0d I-7` are all present — so `AGENTS.md`'s "read
`git log --oneline`" instruction is **not** already false. It shows only in the **first-parent** view,
where PR #1 reads `Merge pull request #1 from adammedford/task/i-3-abstractions-and-doubles` while
every earlier merge named its tasks (`Merge task I-2:`, `Merge tasks 1.0 and 1.6a:`). Decision 5
makes that record a dispatch precondition, so the inconsistency stops being cosmetic, and it is the
one output of this ticket that CI or the hook could cheaply assert later.

### What this resolves elsewhere

- **`/code-review`'s spec axis**, which the map listed as unspecified, is answered by decision 3: for
  a workstream task the spec is **the plan row plus the contracts rows that name it**, cited by path.
  No per-task spec file is needed. Removed from "Not yet specified".
- **G1a now depends on this ticket.** The human decided G1a's checklist includes an item that
  `docs/agents/landing-work.md` exists, so the gate cannot close until these six decisions are
  written into it. That ordering is recorded in ticket 05.

### Handoff notes beyond `landing-work.md`

- **`.githooks/pre-push:9`'s header is a steering defect**: "once per clone and once per worktree" is
  false. Fix it in the same pass as ticket 03's CODEOWNERS guard, which also lands in this file.
- **Vocabulary.** This ticket and the map say **dispatcher**; the frozen plan says **orchestrator** in
  both places it names the role, and `GLOSSARY.md` defines neither. `landing-work.md` is a permanent
  doc, so it uses **orchestrator** and does not introduce a third word. `GLOSSARY.md` also needs
  entries for **session** and **claim**, which this ticket's output leans on, and its **Workstream**
  entry defines a workstream as "one of the six parallel **lanes**" — the word the map bans, in the
  same entry ticket 03 flagged for its neighbouring "team" contradiction. One pass fixes all of it.
