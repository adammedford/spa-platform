# ADR-0030: The workflow test companions run when a workflow file changes, in CI and in the full check

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** integrator
- **Plan ref:** No decision row. #276, raised by #271; confirmed as #324 Q29. **Amends** ADR-0027 decision 2, its rejected
  alternative "Gate the companion's CI step on paths", and its consequence "`tools/check.sh` does
  not run the companion"; and ADR-0026 decision 4's note that `tools/check.sh` is unchanged.

## Context

Every workflow script under `.claude/workflows/` has a test companion beside it
(`<name>_tests.mjs`), seven by 2026-10-22. ADR-0027 put one of them, the overnight script's, in
CI's Documents job on every push with no path condition, and kept `tools/check.sh` out of it (Q32).
Nothing ran the other six: on #271 all of them were run by hand before each fix round. Together
they take about half a minute on node 22.

## Decision

1. **`.github/touches.sh` answers a third question, `workflows`:** whether a change touches
   anything under `.claude/workflows/`, fixtures included, since the companions share them.
2. **CI's Documents job runs every companion when that answer is yes,** in one step gated on the
   decision step's `workflows` output, which is also yes whenever every job runs (no base, or the
   workflow or the classifier changed). #213's overnight-only step is folded into it.
3. **`tools/check.sh --full` runs the same companions under the same answer,** skipping them
   otherwise as it skips the .NET and Terraform halves, unless `--both-halves` is given, and names
   each in its pass line. It fails without node.
4. **A workflow script with no companion beside it fails both,** naming the script, before any
   companion runs.

## Alternatives rejected

- **Keep ADR-0027's unconditional step for the overnight companion and add the rest beside it.**
  Half a minute on every pull request that touches no workflow file, and two rules for one kind of
  file.
- **Run the companions on every push, in CI only.** A lane's green full check would then say
  nothing about a workflow edit, which is how #271's rounds went by hand.
- **A shared helper script both callers run.** One more file with no companion of its own; the
  loop is a dozen lines, and `tools/check_tests.py` runs CI's copy as the workflow holds it.

## Consequences

- A pull request that touches no workflow file no longer runs the overnight companion in CI.
- Adding a workflow script without its companion is red in CI and in every full check that a
  workflow change earns.
- ADR-0027's status line records the amendment.
