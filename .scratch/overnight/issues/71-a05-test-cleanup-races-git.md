# 71: An A-05 test's cleanup races a write into its throwaway repository

**Task:** none (CI flake, seen 2026-10-17 on pull request 44)
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-agent

## Problem

`.github/path_ownership_tests.py` → `test_conflict_resolution_cannot_cross_owners` failed once in
CI with an error, not a failed assertion: removing the test's temporary directory raised
`OSError: [Errno 39] Directory not empty: '.git'`. Every assertion had passed. The rerun passed, the
test passed five of five runs locally, and the pull request it failed on did not touch the test or
`.github/path-ownership.py`. Run: `actions/runs/38988733518`, the first attempt of "Build and test".

A red check that the next run turns green teaches whoever sees it to rerun rather than read, which
is the habit that lets a real failure through.

## Likely cause, unconfirmed

Something was still writing into `.git` when `TemporaryDirectory.cleanup` walked it. The test
merges (`git merge --no-ff --no-commit -s ours`) and then commits, and git can start housekeeping
detached after a commit or merge (`gc --auto`, which detaches by default, or `git maintenance`'s
auto run), which outlives the `git` call the test waited for. That would explain an error only in
cleanup, only intermittently, and only on a runner whose git version runs that housekeeping.

Confirm before fixing: the runner's `git --version`, and whether the failure reproduces under a
loop of the test with housekeeping left on.

## Done when

- [ ] The cause is confirmed or replaced, and stated in the fix's commit.
- [ ] The throwaway repositories every test builds turn off detached housekeeping (for instance
      `gc.auto=0` and `maintenance.auto=false` in the repository's own config), if that is the
      cause, so no git process outlives the call that started it.
- [ ] The same check is made for the other suites that build throwaway repositories, such as
      `tools/roadmap_tests.py`, since they share the shape.
- [ ] The test passes a loop of many runs on the CI runner's git version.
