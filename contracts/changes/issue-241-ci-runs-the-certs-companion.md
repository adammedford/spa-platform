# Change request issue-241: CI runs the test companion of `certs.sh`

- **Raised by:** workstream 1, AWS and IaC, during #241
- **Tasks:** #241; I-5, whose workflow holds every test companion's steps; #243, which adds the cell scripts' companions to CI and is the natural place for this one
- **Date:** 2026-10-22
- **Status:** open

## What changed

#241 makes `tools/scripts/certs.sh` resolve its Region the way `tools/preflight.sh` does (`--region`,
then `AWS_REGION`, then `AWS_DEFAULT_REGION`, then the profile's configured Region) and refuse with a
message rather than fall back to us-east-1. Its first test companion is
`tools/scripts/certs_tests.py`, in the shape D-3 in `docs/dependencies.md` decided for a shell
script's tests: Python's `unittest`, running the real script under `/bin/bash` with an aws double on a
constructed PATH.

The companion needs no AWS CLI, no credentials and no network. It needs `python3` (3.9-compatible),
`/bin/bash`, `openssl`, and `basename`, `dirname`, `sed`, `tr`, `cat`, `cp`, `grep`, `mkdir`, `chmod`,
`mktemp`, `rm`, `uname` and `sleep` on the runner, which both CI images carry. It honours
`SCRIPT_TESTS_REQUIRE_BASH` as the other companions do. On this Mac it runs 14 tests in about 7
seconds under bash 3.2.

`tools/check.sh --full` runs it when a change touches `certs.sh`. Nothing in CI runs it.

## What is asked of the integrator

Two edits in files AGENTS.md reserves to the integrator, the same two change request 1.6b asks for
its companion, and best made in #243 alongside those:

1. **Two steps in `.github/workflows/ci.yml`**, in the shape of `tools/preflight_tests.py`'s pair:
   - in the Documents job, `python3 -I tools/scripts/certs_tests.py`, under the runner's bash 5;
   - in the Scripts under bash 3.2 job, the same command with `SCRIPT_TESTS_REQUIRE_BASH: "3.2"`.
2. **D-3 in `docs/dependencies.md`**, one sentence in the shape of its others:
   "`tools/scripts/certs.sh` has `tools/scripts/certs_tests.py` (#241), in both jobs: an aws double
   and the real openssl, so no case reaches AWS."

## What #241 did without it

The script change and the companion, all of it. The companion passes under `tools/check.sh --full`;
before the script change, ten of its fourteen cases failed against the us-east-1 fallback.

## Why this is a change request and not an edit

`.github/` and `docs/dependencies.md` are the integrator's (AGENTS.md, OWNERSHIP.md).

## Blocking

Nothing at runtime. Until this lands the companion runs only in the local full check, so a later
edit to `certs.sh` that brings the fallback back is caught there and not in CI, and #241 cannot be
done in AGENTS.md's sense, which asks for its tests to pass in CI and no change request open.
