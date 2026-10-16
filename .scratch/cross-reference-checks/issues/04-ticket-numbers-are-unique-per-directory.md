# 04: Ticket numbers are unique within a feature directory

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** Two sessions filing concurrently cannot both claim the same ticket number
without it failing before the second one is pushed, and a session that finds its number taken has
a documented answer rather than a check that only reports the collision. When this was filed,
`.scratch/overnight/issues/` held two files numbered `61` and two numbered `66`, written by
sessions that could not see each other's unlanded work, and a citation of either number was
ambiguous. A commit on main (`113c4a3`) has since renamed the second `66`, the G2
session-composition ticket, to `67`, so only the `61`s remain.

Numbering restarts per feature directory by design, so the check is scoped to one
`.scratch/<feature-slug>/issues/` at a time rather than across the whole tracker.

**The live collision has to be resolved for this to land green**, and renumbering moves a file
that other documents cite by number. This spec's own source ticket was one of the two `66`s and
kept the number, so it is not among them. Fix the citations in the same change as the rename.

**Blocked by:** 01 (the test companion). Shares a file with 02 and 03, so they land one at a time,
but neither gates this. Lands after 03 if both are taken, since 03's citation rule reads the
numbers this one renames.

**Status:** ready-for-agent

- [ ] The check fails when two files share a leading number inside one
      `.scratch/<feature-slug>/issues/`, and the failure names the directory and both files.
- [ ] A number repeated across two different feature directories is not a failure.
- [ ] The `61` collision in `.scratch/overnight/issues/` is renumbered, and every
      document citing a renamed ticket by number is updated in the same change.
- [ ] `docs/agents/issue-tracker.md` states what a session does when the number it wants is
      already claimed, so concurrent filing has an answer and not just a failing check.
- [ ] A red case with two files sharing a number, a green case with the number repeated across
      directories, and a green case on the current tree, in `.github/steering_tests.py`.
