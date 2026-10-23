# Change request issue-308: CI runs the test companion of `verify-teardown.sh`

- **Raised by:** workstream 1, AWS and IaC, during #308
- **Tasks:** #308; #243, which adds the cell scripts' companions to CI and is the natural place for
  this one
- **Date:** 2026-10-23
- **Status:** decided, 2026-10-23, on
  https://github.com/adammedford/spa-platform/issues/232#issuecomment-6375865081.
  Both items accepted as asked; they land with this Status. See the Decision section below.

## What changed

#308 adds `tools/scripts/verify-teardown.sh`, which `teardown.sh` ends by calling, and its companion
`tools/scripts/verify_teardown_tests.py`, in D-3's shape (`docs/dependencies.md`): Python's
`unittest`, running the real script under `/bin/bash` against the shared doubles in
`tools/scripts/cell_doubles.py`, so no case reaches AWS. It honours `SCRIPT_TESTS_REQUIRE_BASH` as
the other cell companions do, and runs 22 cases in about ten seconds on this Mac under bash 3.2.

`tools/check.sh --full` runs it when a change touches the script, the companion or the doubles.
CI runs `teardown_tests.py`, whose cases now run `verify-teardown.sh` at the end of every teardown,
but not this companion, which holds the readers' own cases (#118 step 2's records, the lag, a
standing role by tag and by name, a describe refused).

## What is asked of the integrator

1. **Two steps in `.github/workflows/ci.yml`**, beside `teardown_tests.py`'s pair:
   - in the job that runs the cell companions under the runner's bash 5,
     `python3 -I tools/scripts/verify_teardown_tests.py`;
   - in `scripts-bash-3-2`, the same command with `SCRIPT_TESTS_REQUIRE_BASH: "3.2"`.
2. **D-3 in `docs/dependencies.md`**, one sentence beside the cell scripts' line:
   "`tools/scripts/verify-teardown.sh` has `tools/scripts/verify_teardown_tests.py` (#308), in both
   jobs, against the shared doubles."

## What #308 did without it

The script, the companion and `teardown.sh`'s call, all green under `tools/check.sh --full`.

## Why this is a change request and not an edit

`.github/` and `docs/dependencies.md` are the integrator's (AGENTS.md, OWNERSHIP.md).

## Blocking

Nothing at runtime. Until it lands a later edit that breaks a reader is caught by the local full
check and by `teardown_tests.py` in CI, but not by the reader's own cases in CI.

## Decision

Integrator, 2026-10-23, on Adam's confirmed answer,
https://github.com/adammedford/spa-platform/issues/232#issuecomment-6375865081: **both items accepted as asked.**

1. `.github/workflows/ci.yml` runs `python3 -I tools/scripts/verify_teardown_tests.py` in the cell
   companions' job under bash 5, beside `teardown_tests.py`, and again in `scripts-bash-3-2` with
   `SCRIPT_TESTS_REQUIRE_BASH: "3.2"`.
2. D-3 in `docs/dependencies.md` gains the sentence as proposed.
