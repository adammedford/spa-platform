# 05: `tools/land.sh` carries a merge body

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** Landing a pull request through the sanctioned path can carry the merge body the
guide requires. `docs/agents/landing-work.md` says a merge body carries `Outstanding:` lines, and
`tools/land.sh` accepts only `--subject`, so five merges in one session were landed by running the
script's sequence by hand -- fetch, fast-forward, `tools/roadmap.py`, commit the refresh, push --
which is precisely the error path the script exists to prevent. After this ticket the script can
express what the guide asks for, so there is no reason to step around it.

**Blocked by:** None (can start immediately).

**Status:** resolved

- [x] `tools/land.sh --body-file <path>` passes the file through to `gh pr merge --body-file`.
- [x] A missing value and an unreadable path both fail before the merge, like the existing
      `--subject` handling: a wrong argument found afterwards leaves the merge done.
- [x] The existing `--subject` handling and the "no subject given" confirmation prompt are
      unchanged.
- [x] The header comment documents the option, says the body carries the `Outstanding:` lines, and
      points at `docs/agents/landing-work.md` so the requirement is findable from the script.
- [x] `--help` shows the new option; it prints a line range of the header, so the range tracks.
- [x] Still bash 3.2, the shell macOS ships.
- [x] No tests: the repository has no shell test harness, and adding one is a larger piece of work
      filed separately.

## Answer

Landed on `integrator/g2-prep` in fb6b66d. `--body-file` is validated before the merge and passed through to `gh pr merge`. `--help` now derives the header rather than printing a fixed line range, which had already gone stale. No automated test: the repository has no shell harness.
