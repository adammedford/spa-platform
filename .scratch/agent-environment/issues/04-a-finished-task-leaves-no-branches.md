# 04: A finished task leaves no branches behind

**Owner:** integrator
**Blocked by:** 03 (the roadmap's JSON mode).
**Status:** moved to #71

**What to build:** `docs/agents/landing-work.md` says a task's claim branch is deleted when the task
is done, and nothing deletes it. Once the merge that lands a row as done reaches `main`, that row's
claim branches become eligible for cleanup locally and on `origin`; an overnight integration
branch becomes eligible once its pull request has merged. Both follow the preservation rules below. A part-landed row keeps its claim, because the claim is how the next session
knows the row is taken.

The leftovers cost work. On 2026-10-17 this checkout held 44 `task/*` branches, 19 of them not
ancestors of `main`. Five of that night's tickets needed dated branch names because older branches
of the same names sat at stale heads; collect had to `git cherry` the branches of unlanded rows to
tell a claim from a leftover; and `overnight/2026-10-16` looked unmerged, though every commit on it
was already on `main` in equivalent form.

- Deletion reads each row's state from ticket 03's JSON mode, so "done" means what the roadmap says.
- A branch is deleted only when its current tip's work is on `main`, as an ancestor or as
  patch-equivalent commits, including any merge-resolution changes. Unmerged or uncertain work
  is listed and kept, even when an earlier pull request from the branch has merged.
- Branches checked out in any worktree are listed and kept; cleanup never forces worktree removal.
  Remote deletion checks that the tip has not changed since verification and preserves a branch
  when that check fails.
- A one-off pass removes the current leftovers by the same rule.
- landing-work.md names the mechanism beside the rule it carries out.

- [ ] Landing a row as done deletes eligible claim branches locally and on `origin`, preserving
      and listing branches with unmerged or uncertain work or checked out in a worktree.
- [ ] A part-landed row's claim branch survives the same landing.
- [ ] After the one-off pass, every remaining `task/*` branch is the claim of an open or
      part-landed row, carries unmerged or uncertain work, or is checked out in a worktree.
- [ ] After its pull request merges, an overnight branch is deleted only if its current tip's
      work is verified on `main` and it is not checked out in a worktree; otherwise it is listed
      and kept. A concurrent remote tip change also preserves the branch.

## Comments

- 2026-10-17: moved to #71 under ADR-0020; this file is the archive copy.
