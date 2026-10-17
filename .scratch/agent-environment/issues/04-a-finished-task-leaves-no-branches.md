# 04: A finished task leaves no branches behind

**Owner:** integrator
**Blocked by:** 03 (the roadmap's JSON mode).
**Status:** ready-for-agent

**What to build:** `docs/agents/landing-work.md` says a task's claim branch is deleted when the task
is done, and nothing deletes it. Once the merge that lands a row as done reaches `main`, that row's
claim branches go, locally and on `origin`, and an overnight integration branch goes once its pull
request has merged. A part-landed row keeps its claim, because the claim is how the next session
knows the row is taken.

The leftovers cost work. On 2026-10-17 this checkout held 44 `task/*` branches, 19 of them not
ancestors of `main`. Five of that night's tickets needed dated branch names because older branches
of the same names sat at stale heads; collect had to `git cherry` the branches of unlanded rows to
tell a claim from a leftover; and `overnight/2026-10-16` looked unmerged, though every commit on it
was already on `main` in equivalent form.

- Deletion reads each row's state from ticket 03's JSON mode, so "done" means what the roadmap says.
- A branch is deleted only when its work is on `main`, as an ancestor or as patch-equivalent
  commits; anything else is listed and kept.
- A one-off pass removes the current leftovers by the same rule.
- landing-work.md names the mechanism beside the rule it carries out.

- [ ] Landing a row as done deletes its claim branches locally and on `origin`.
- [ ] A part-landed row's claim branch survives the same landing.
- [ ] After the one-off pass, every remaining `task/*` branch is the claim of an open or
      part-landed row, or carries work that is not on `main`.
- [ ] An overnight branch is gone once its pull request has merged.
