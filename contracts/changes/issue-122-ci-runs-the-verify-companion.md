# Change request issue-122: CI runs the test companion of `verify-release-immutability.sh`

- **Raised by:** workstream 1, AWS and IaC, during #122 (task 1.5's verification script, for #92)
- **Tasks:** 1.5; I-5, whose workflow holds every test companion's steps
- **Date:** 2026-10-19
- **Status:** decided, 2026-10-22, on
  https://github.com/adammedford/spa-platform/issues/226#issuecomment-6319841927.
  Accepted as asked; both edits are #243's. See the Decision section below.

## What changed

#122 adds `tools/scripts/verify-release-immutability.sh`, P-04 against the applied platform bucket,
and its test companion, `tools/scripts/verify_release_immutability_tests.py`, in the shape D-3 in
`docs/dependencies.md` decided for a shell script's tests: Python's `unittest`, running the real
script under `/bin/bash` with doubles on a constructed PATH.

The companion needs no AWS CLI, no Terraform, no credentials and no network: its doubles stand in for
`aws`, `terraform` and `date`, and the doubled AWS keeps the case's bucket, policy, repositories and
teardown role in a file. It needs `python3` (3.9-compatible), `/bin/bash`, and `awk`, `cat`,
`dirname`, `mktemp`, `rm`, `sed` and `tr` on the runner, which both CI images carry. It honours
`SCRIPT_TESTS_REQUIRE_BASH` as the other companions do. On this Mac it runs 33 tests in about 75
seconds under bash 3.2.

`.github/shell-parse.py` already reads the script in both jobs, because it reads every tracked
`.sh`, so its parse needs nothing new. Nothing runs the companion.

## What is asked of the integrator

Two edits in files AGENTS.md reserves to the integrator.

1. **Two steps in `.github/workflows/ci.yml`**, in the shape of `tools/preflight_tests.py`'s pair:
   - in the Documents job, `python3 -I tools/scripts/verify_release_immutability_tests.py`, under
     the runner's bash 5;
   - in the Scripts under bash 3.2 job, the same command with `SCRIPT_TESTS_REQUIRE_BASH: "3.2"`.

   The Documents job is the place because it runs on every change: the script is under
   `tools/scripts/`, which neither half in `.github/touches.sh` covers. The workflow's header lists
   the companions it runs and would name this one too.
2. **D-3 in `docs/dependencies.md`** lists each script's companion and the jobs that run it. One
   sentence in that shape would do: "`tools/scripts/verify-release-immutability.sh` has
   `tools/scripts/verify_release_immutability_tests.py` (#122), in both jobs: doubles for aws,
   terraform and date, so no case reaches AWS."

## What #122 did without it

The script and the companion, all of it. The companion passes by hand,
`python3 -I tools/scripts/verify_release_immutability_tests.py`, and each of its guards was broken on
purpose to watch it fail; the mutations and their counts are in the task's Answer on #122.

## Why this is a change request and not an edit

`.github/` and `docs/dependencies.md` are the integrator's (AGENTS.md, OWNERSHIP.md).

## Blocking

Not the sitting: Adam runs the script against the applied bucket whether or not CI runs its
companion. Until this lands the companion runs only by hand, so a later edit to the script can break
a case with nothing to say so, and the row cannot be done in AGENTS.md's sense, which asks for its
tests to pass in CI and no change request open.

## Decision

Integrator, 2026-10-22, on Adam's confirmed answer,
https://github.com/adammedford/spa-platform/issues/226#issuecomment-6319841927
(Q3 of #193's questions on #226): **accepted as asked, decided with change requests 1.6b and 1.7.**
He chose this over leaving it for a ticket of its own, which would have left one `tools/scripts/`
companion manual while CI runs the others.

1. **The two `ci.yml` steps** for `tools/scripts/verify_release_immutability_tests.py`, one per
   job, are #243's to land, with the workflow's header naming the companion.
2. **The D-3 sentence** in `docs/dependencies.md` is #243's too, in the words item 2 gives.

Neither edit has landed when this Status is written; #243 cites this decision by commit.
