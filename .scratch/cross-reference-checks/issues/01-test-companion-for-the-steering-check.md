# 01: Give the steering check a test companion

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** Running the test suite proves that the steering check still catches the two
defects it was written for. Today `.github/steering.py` is the only check in its family shipping
with no tests, so a session widening it has no way to see that it broke the rules already there.
After this ticket, each of those rules fails on a tree built to trip it, and CI runs those cases
immediately before the check itself.

This is a prefactor. Nothing widens `steering.py`'s charter until breaking it is visible.

**Blocked by:** None (can start immediately).

**Status:** resolved

- [x] `.github/steering_tests.py` builds a throwaway tree per case rather than asserting against
      the live repository, following `.github/path_ownership_tests.py`: the module resolved from
      `__file__` so its root can be patched, output captured through `contextlib` and `io`.
- [x] A progress claim in a steering file fails the check, and a clean pair of files passes it.
      The red case uses a shape the drift actually took, not an invented one.
- [x] A backticked path that resolves to nothing fails the check; a resolving one passes.
- [x] A path listed in `PLANNED` is exempt while absent, and fails the check once it exists, so
      the list cannot outlive its entries.
- [x] Assertions are on the check's exit code and printed output, not on the parsing helpers
      underneath, so a test pins a rule rather than an implementation detail.
- [x] Wired into the CI step that runs the check, immediately before it, as
      `.github/plan-secrecy-tests.py` and `tools/roadmap_tests.py` already are.
- [x] `tools/preflight.sh` and the pre-push hook are untouched: the tests are a CI concern, and the
      hook's one-second tier runs the check, not its tests.

## Answer

Landed on `integrator/g2-prep` in bafe4b6. `.github/steering_tests.py` holds the two existing rules with one red case per progress pattern; deleting each of the ten rules in turn turns the suite red. Wired into CI before the check.
