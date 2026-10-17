# 69: A test harness for the repository's shell scripts

**Task:** none (raised by overnight ticket 66, "Out of Scope")
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-human

## Problem

The shell scripts the landing path depends on have no automated test. `tools/preflight.sh`
shipped five defects in one session, and its agreement with what it parses is checked only by
running it by hand. `tools/land.sh` is the one sanctioned merge path, and the `--body-file` it
gained (cross-reference-checks ticket 05) was verified by extracting its parse loop into a scratch
script, which a later edit can drift away from. A review on 16 October found a real defect in it
that no test could have caught: a relative body path was checked from one directory and read from
another (`2c9962f`).

## The decision

`ready-for-human` because it needs a tool, and a new tool is a `docs/dependencies.md` decision:

- **bats-core** — the usual choice, a dependency CI and every machine must install.
- **Plain bash test scripts** in the idiom of `.github/*_tests.py` — no new tool, but no assertion
  library either.
- **Python driving the scripts as subprocesses**, with `gh` and `git` stubbed on `PATH` — matches
  the existing test family and runs where they already run. Note the agent worktree sandbox
  refused a `PATH`-stubbed `git`; CI would not.

Whichever is chosen must run under the bash 3.2 macOS ships, since both scripts target it.

## Done when

- [ ] A harness is chosen and recorded in `docs/dependencies.md`.
- [ ] `tools/land.sh`'s argument parsing and its pre-merge refusals are tested by running the
      script itself, not a copy of its loop.
- [ ] CI runs it beside the Python checks.
