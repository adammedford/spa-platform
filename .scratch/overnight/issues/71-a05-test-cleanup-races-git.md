# 71: An A-05 test's cleanup races a write into its throwaway repository

**Task:** none (CI flake, seen 2026-10-17 on pull request 44)
**Owner:** integrator
**Blocked by:** None
**Status:** resolved

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

- [x] The cause is confirmed or replaced, and stated in the fix's commit. Commit `7cf230a`;
      see Answer.
- [x] The throwaway repositories every test builds turn off detached housekeeping (for instance
      `gc.auto=0` and `maintenance.auto=false` in the repository's own config), if that is the
      cause, so no git process outlives the call that started it. `Repo.__init__`; the steering
      suite's trees start none (Answer).
- [x] The same check is made for the other suites that build throwaway repositories, such as
      `tools/roadmap_tests.py`, since they share the shape. Traced; none is exposed (Answer).
- [ ] The test passes a loop of many runs on the CI runner's git version. Open: looped on
      2.54.0 only, not the runner's 2.55.0 (Answer, Outstanding).

## Answer

Branch `task/ticket-71-a05-test-cleanup-races-git`, commit `7cf230a`, one file:
`.github/path_ownership_tests.py`. Box 4 stays open; see Outstanding.

**Cause, confirmed, and stated in commit `7cf230a`.** Git 2.54 made "geometric" the default strategy
for automatic maintenance (`Documentation/RelNotes/2.54.0.adoc`). Every `git commit` and `git merge`
ends by starting `git maintenance run --auto --quiet --detach` (`run-command.c`,
`prepare_auto_maintenance`), which takes its lock, detaches, and only then runs its background
tasks. In a repository holding no packs, the geometric-repack task fires once two loose objects
share the `objects/17` shard: its threshold defaults to 100, rounds up to 256, and is compared with
that shard's count times 256 (`builtin/gc.c`, `geometric_repack_auto_condition`;
`odb/source-loose.c`). Commit ids depend on the clock, so any commit can trip it. The repack then
writes `.git/objects/pack`, `.git/objects/info/packs` and `.git/info/refs` after the call the test
waited for has returned, recreating directories `TemporaryDirectory.cleanup` has already removed,
and the final `rmdir('.git')` fails. The ticket's hypothesis was right in kind. The writer is the
geometric repack, which needs git 2.54 or later: before that the detached half ran only the gc
task, whose condition never fires in a repository this small.

The runner was the ubuntu-24.04 image 20261012.334.1 with git 2.55.0 and Python 3.12 (run
38988733518, attempt 1, whose traceback ends in `os.rmdir(entry.name, dir_fd=topfd)` on `.git`).
From 2.54 to 2.55, `builtin/gc.c` changes by a seven-line refactor, and `run-command.c` adds a
fallback that reads `gc.auto<=0` as off when `maintenance.auto` is unset. The mechanism is the same.

**Evidence**, on Apple git 2.54.0, with GitHub Desktop's bundled git 2.53.0 as the control:
- One traced run of the suite before the fix: 57 `git maintenance run --auto` started, and all 57
  detached.
- One traced commit holding two objects in `objects/17`: after it returned, the detached process ran
  `git repack -d -l --cruft ... --write-midx`, two `pack-objects` and a `multi-pack-index write`.
- A reproduction of that commit, cleaned up 0 to 60 ms after it returned:
  - housekeeping on: 17 of 300 cleanups failed with ENOTEMPTY. Five were on `.git` itself, each
    leaving `objects/info/packs`; the rest were on `pack`, `objects` or `info`. A second round
    gave 18 of 300, with 8 on `.git`, each leaving the repack's `objects/pack/tmp_pack_*`.
  - `maintenance.auto=false`: 0 of 300, twice, with `git commit` at 11 ms median.
  - `maintenance.autoDetach=false`: 0 of 300, with `git commit` at 38 ms median, because the
    repack now runs inside the commit (16 ms with the defaults).
  - git 2.53.0 with housekeeping on: 0 of 300. Maintenance still detached, but did nothing.
- The whole suite, looped: 0 of 300 before the fix and 0 of 300 after, serially, then 0 of 235
  each way under 3 and 4 parallel workers. The flake does not show at suite level on this machine,
  so these loops cannot tell the fix apart; the reproduction above is what does. They do show the
  new case holding steady across repetitions, and it also passed 50 of 50 runs on its own.
- On git 2.53.0 too, the new case fails without the fix (`maintenance` started) and passes with
  it. That binary's one other error, the octopus case, is GitHub Desktop's relocated bundle
  missing `git-merge-octopus`, and it fails the same way with or without the fix.

**What landed:**
- `Repo.__init__` sets `maintenance.auto=false` in each throwaway repository's own config.
- `ThrowawayRepositoryTests.test_a_commit_or_a_merge_starts_no_housekeeping` drives a commit and a
  merge through `Repo` under `GIT_TRACE2_EVENT` and asserts that no `maintenance` or `gc` process
  started. It also asserts that the trace saw the commit and the merge, so its silence counts.
- Mutations: deleting the config line fails the case (1 of 16), and so does swapping it for
  `maintenance.autoDetach=false` (1 of 16).

**The other suites (box 3)**, each traced over a full run:
- `.github/steering_tests.py` builds 48 trees that run only `init`, `add` and `ls-files`. They start
  no housekeeping.
- `tools/roadmap_tests.py` and `.github/plan-secrecy-tests.py` start no git process at all.

None of them changed.

**Seams (chosen by implementer):**
1. `Repo` in `.github/path_ownership_tests.py`, observed through git's trace2 event stream
   (`cmd_name` events). It catches any housekeeping that a commit or a merge through `Repo` starts,
   on any git version. It misses a background git process that is not housekeeping, such as an
   fsmonitor daemon a developer's global config could start; none is configured here or on CI.
2. The suite's cleanup under repetition, observed by loops. This is statistical evidence for the
   record, not a test in CI.

**Decided by implementer:**
- **`maintenance.auto=false` alone, not also `gc.auto=0`**, although the ticket's example named
  both. In git 2.55 every command that starts automatic housekeeping reads `maintenance.auto` first:
  commit, merge, am, rebase, fetch and receive-pack all go through `prepare_auto_maintenance`.
  Nothing these suites run calls `git gc --auto` directly, and `gc.auto` does not reach the
  geometric-repack task that raced. The second line would change nothing on any git this suite
  supports (2.36 or later, for `--remerge-diff`), and no assertion could fail for it. Adding the
  line reverses this.
- **Housekeeping off, not moved to the foreground.** `maintenance.autoDetach=false` also removes the
  race (0 of 300 above), but it keeps the repack inside the commit, at 38 ms against 11. Its
  check could only look for a detach, which trace2 marks only from git 2.47. Turning housekeeping
  off lets the case assert that none starts, on every git.
- **The case reads trace2.** Racing the cleanup would pass nearly every time. Asserting the config
  value would restate the fix rather than read git's behaviour.
- **`.github/steering_tests.py` is unchanged.** Its trees start no housekeeping, so a config line
  there would change nothing observable, and no assertion could fail for it. A future case there
  that commits or merges needs the same line `Repo` has.
- **The gate was run with `-exec dirname {} \;`.** The task text has `-exec dirname {} ;`,
  which zsh, the shell the gate runs in, rejects as a parse error before anything runs. `\;` is
  how `ci.yml` writes it. That branch runs only when `infra/` changed, and here it did not.

**Gate:** `GATE: GREEN` at `7cf230a`, in `.scratch/overnight/log/ticket-71-implement.txt`. Test
summary: "total: 1516, failed: 0, succeeded: 1516, skipped: 0". Steering was clean, and A-05
reported "1 file(s), all owned by integrator."

**Outstanding:** box 4, the loop on git 2.55.0 itself. The loops ran on 2.54.0, which shares the
mechanism. A loop on 2.55 needs CI, and nothing is pushed tonight, or a 2.55 binary, and none is
installed here; building or installing one was outside tonight's rules. The new case is
deterministic and runs in CI on 2.55.0 at the first push. A green there shows that no housekeeping
starts on the runner's git, which is the race's precondition.

## Review

**Reviewed 2026-10-17** against fixed point `eb65be8`, implementation `7cf230a`, by a session that
did not write it. No sub-agents were available, so I ran both axes one after the other and kept them
apart. Review commit `1cebdd1` ("Review: name the fsmonitor exception in ThrowawayRepositoryTests")
changes one docstring in `.github/path_ownership_tests.py`, an integrator path. This ticket stays
uncommitted in the main checkout.

**Gate.** The implement log's `HEAD=` matched `7cf230a` and ended `GATE: GREEN`, so I copied it
rather than rebuilding. After the fix the gate ran once into
`.scratch/overnight/log/ticket-71-review.txt`. The run is `HEAD=1cebdd1…`, and its results:
- total 1516, failed 0, succeeded 1516, skipped 0;
- steering clean;
- `A-05: 1 file(s), all owned by integrator.`;
- `GATE: GREEN`.

The run used the implementer's transcription fix, `-exec dirname {} \;`. `zsh -n` rejects the
unescaped form with `parse error near '|'`. No `infra/` file changed, so that branch did not run.

The gate does not run the Python suites, so I ran `.github/path_ownership_tests.py` myself at
`1cebdd1`. All 16 cases passed, and the new case passed 25 of 25 runs on its own.

**Spec sources.** The ticket's Task is none. So no row in `docs/plan/03-workstreams-and-tasks.md`
and no `contracts/` row names it, and the spec is this ticket. A-05 (`docs/plan/04-tests.md`) is the
test whose suite flaked.

### Standards

Held to `CODING_STANDARDS.md`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`, `docs/agents/review.md`,
the smell baseline and `.editorconfig`.

1. **Fixed: a universal without its exception** (CODING_STANDARDS.md -> "A universal names its
   exceptions by the writer"). *Verified against the vendor's documentation; not run.*
   - The `ThrowawayRepositoryTests` docstring opened "Repo runs no git housekeeping, so no git
     process outlives the call that started it". The case checks only that no `maintenance` and no
     `gc` started.
   - A developer whose own config sets `core.fsmonitor=true` gets a `git fsmonitor--daemon` that
     commands start in the background and leave running, and the case does not look for it. The
     daemon's documentation says commands "automatically start it (if necessary)":
     https://git-scm.com/docs/git-fsmonitor--daemon.
   - The Answer's first seam named this exception. The code did not.
   - The docstring now names the exception and cites that page.
   - Nothing in the repository or `ci.yml` sets `core.fsmonitor`, and it is unset on this machine.
   - I did not run it, because that would mean starting a daemon from a borrowed global config.

Checked and holding, no finding:
- **"An assertion must be able to fail", and the mutation is named.** *Verified by mutation*, on
  Apple git 2.54.0 against scratch copies of the suite:
  - Deleting the config line fails 1 of 16 (`'maintenance' unexpectedly found`).
  - Swapping it for `maintenance.autoDetach=false` also fails 1 of 16.
  - So does a third swap, `gc.auto=0`. That setting turns maintenance off only from 2.55, which
    reads it when `maintenance.auto` is unset.
  - The `commit` and `merge` assertions catch a trace that went silent by accident.
- **"A guard reads the thing it guards".** The case reads git's trace2 events, not the config value.
- **"Code that rests on vendor behaviour cites the vendor".** *Verified by fetching each.* All five
  URLs resolve and say what they are cited for:
  - The git-config anchor `Documentation/git-config.txt-maintenanceauto` exists.
  - The api-trace2 anchor `#_event_format` exists.
  - The 2.54.0 release notes, `config/maintenance.adoc` at v2.55.0 and `builtin/gc.c` at v2.55.0
    exist.
- **Ownership.** One file, the integrator's.
- **Style.** The new lines are at most 99 columns, with no tabs and no trailing space.
- **Smells.** None worth raising.
  - `assertNotIn("gc", started)` cannot fail on its own from git 2.29, where `gc` only runs under
    `maintenance`.
  - Before 2.29 it is the assertion that fails, because `commit` ran `gc --auto` directly. So it
    stays.

### Spec

1. **Box 1, met.** Commit `7cf230a` states the cause, and every part of it holds.
   - *Checked in the release notes:* the 2.54.0 notes say `"git maintenance" starts using the
     "geometric" strategy by default`.
   - *Checked in git's v2.55.0 source:*
     - `prepare_auto_maintenance` reads `maintenance.auto` first. It falls back to `gc.auto` only
       when `maintenance.auto` is unset.
     - Across the whole v2.55.0 C tree, its only callers are commit, merge, am, rebase, fetch and
       receive-pack. Nothing spawns `gc --auto` directly.
     - `maintenance_run_tasks` takes the lock, runs the foreground tasks, then daemonizes.
     - `too_many_loose_objects` compares `objects/17`'s count times 256 against
       `DIV_ROUND_UP(100, 256) * 256`. So the second object in that shard trips it.
   - *Checked in the CI log:* run 38988733518, attempt 1, ran on ubuntu-24.04 image 20261012.334.1
     with `git version 2.55.0`. It failed in `os.rmdir(entry.name, dir_fd=topfd)` with
     `OSError: [Errno 39] Directory not empty: '.git'`.
   - *Checked between versions:* from v2.54.0 to v2.55.0, `gc.c` changes by a seven-line refactor
     and `run-command.c` gains the `gc.auto` fallback, as the Answer says.
   - *Reproduced on 2.54.0* with two blobs in `objects/17`, cleaned up 0 to 60 ms after the commit:
     - Housekeeping on: 12 of 800 cleanups failed with ENOTEMPTY (4 on `.git`, 4 on `pack`, 3 on
       `objects`, 1 on `info`). The leftovers held `objects/info/packs`.
     - `maintenance.auto=false`: 0 of 600 failed.
     - A traced run of the suite before the fix started 57 maintenance runs, and all 57 entered the
       `detach` region.
2. **Box 2, met.** `Repo.__init__` turns housekeeping off before its first commit. In a traced run
   of the fixed suite, none of the 15 rule cases started `maintenance` or `gc`. I read the box as
   covering the A-05 suite's repositories, with box 3 covering the rest. Read more widely, its
   purpose still holds for the steering suite's trees, which start no housekeeping.
3. **Box 3, met.** *Traced with `GIT_TRACE2_EVENT` over full runs:*
   - `.github/steering_tests.py` ran `init` 48 times, `add` 55 times and `ls-files` 56 times, and
     nothing else.
   - `tools/roadmap_tests.py` and `.github/plan-secrecy-tests.py` started no git process.
   - A grep of the tracked tree found no C# or shell suite that runs git.
4. **Box 4, not met, and rightly left unticked.** There is no git 2.55 binary on this machine. I
   searched Homebrew, Xcode, the Command Line Tools and the apps that bundle git. The box stays
   outstanding until the first CI run. CI's run annotation says `ubuntu-latest` moves to Ubuntu 26
   from 2026-10-19, so that run may bring a newer git. The deterministic case holds on whichever git
   it gets.
5. **Scope.** No scope creep. The diff is the config line, its comment, the new case and its trace
   helper.
6. **Decisions.** There is no "## Decisions taken" section. All five "Decided by implementer" items
   are gap decisions, not human-owned: no plan row, contract or ADR bears on them, and each is
   reversed by changing one line. I re-weighed each one and upheld all five.
   - **`maintenance.auto=false` alone.** Once it is set, neither 2.54 nor 2.55 reads `gc.auto` to
     decide. `gc.auto=0` on its own does not fix 2.54, as the mutation above shows.
   - **Housekeeping off rather than moved to the foreground.** With `autoDetach=false`, maintenance
     still starts, and the case fails it.
   - **The case reads trace2.** Upheld for the reason in Standards.
   - **`steering_tests.py` unchanged.** Upheld on the trace in box 3.
   - **The gate's `\;`.** Upheld on `zsh -n`, as above.

**Fixed:** Standards 1, in `1cebdd1`. The change is to a docstring only.

**Skipped:** no verified defect was skipped. One idea is outside this ticket's cause and was not
pursued: making the throwaway repositories ignore the developer's own config, with
`GIT_CONFIG_GLOBAL` and `GIT_CONFIG_NOSYSTEM`. That would close the fsmonitor exception and others
like it, but it is a hermeticity change and needs a ticket of its own.
