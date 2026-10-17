# 69: A test harness for the repository's shell scripts

**Task:** none (raised by overnight ticket 66, "Out of Scope")
**Owner:** integrator
**Blocked by:** None
**Status:** moved to #66

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

## Grilling record

Run on paper on the night of 16 to 17 October 2026, in the 2026-10-17 overnight run. Adam is asleep
and on 2026-10-15 accepted every recommendation in advance, so each round adopts its recommendations
as decided and the next round is computed from them. Facts come from `origin/main` at `eb65be8`
(local `main` is identical), `git log`, the files named, and the vendor pages in F10 to F12. Every
behaviour marked "probed" was run in a scratch directory outside the repository, under `/bin/bash`
3.2.57 or `/usr/bin/python3` 3.9.6. Nothing ran `dotnet`, and nothing in the repository changed but
this file.

### Facts, as checked

| # | Fact | What it does to the ticket |
| --- | --- | --- |
| F1 | `tools/land.sh` is 125 lines under `set -euo pipefail`. Its merge call, `gh pr merge`, is line 99. Fifteen `die` lines sit above it: 38, 42, 43, 44, 51 and 52 in the parse loop, then 56, 57, 58, 65, 68, 77, 79, 81 and 88. One sits below it: 104, the fast-forward. Every git call it makes (`rev-parse`, `fetch`, `diff`, `merge`, `add`, `commit`, `push`) works offline against a local bare repository. Only `gh` reaches GitHub. | git needs no double. A throwaway repository with a bare `origin` exercises every pre-merge git call for real. |
| F2 | Three exits before line 99 print no `land:` line: outside a repository (`git rev-parse --show-toplevel` fails under `set -e`), `gh pr view` failing, and end of input at the subject prompt. Probed under `set -euo pipefail`: `read -r reply` at end of input ends the script with status 1 before the `case` runs, so "stopped. Pass --subject ..." never prints. | A pre-merge refusal is any exit before line 99, not only a `die`. A session with no terminal that omits `--subject` sees the prompt, then status 1, and no reason. |
| F3 | Line 47, which 2c9962f added, makes the body path absolute with `cd "$(dirname "$2")" && pwd`. Probed under land.sh's own options, two ways. First, `CDPATH` exported, naming a directory that also holds `sub/`: `--body-file sub/body.md` passes `[ -f ]` against `./sub`, then `cd` resolves `sub` through `CDPATH` and prints it. The path handed on is two lines naming the other directory, and no file is there. Second, a relative name starting with `-` (`-x.md`): it passes `[ -f ]` and `[ -r ]`, then `dirname` and `basename` reject it as an option, and `set -e` exits 1 with their usage text. `CDPATH= cd -- "$(dirname -- "$2")"` with `basename -- "$2"` resolves both (probed). `tools/preflight.sh` line 116 uses the same idiom. | 2c9962f's defect (checked from one directory, read from another) survives its fix under one more condition. The case that shows it reaches the merge call, so it is not a pre-merge refusal. |
| F4 | `tools/preflight.sh`: 7a364fe, eb39fec and 9ec9aa1, all on 15 October, record eight behaviour fixes, each verified by hand: six in 7a364fe, the `export` prefix in eb39fec, a leading indent in 9ec9aa1. 7a364fe lists nine guard cases and the parse forms it compared with `set -a; .`. The script finds `aws-project.env` beside itself through `$0`, which 9ec9aa1 left "alone deliberately". It calls `curl` against nuget.org and probes nine other tools. | "Five defects" undercounts. A preflight test has a different shape from land.sh's: an `aws` double, a whole-file copy beside a crafted env file, and no network. |
| F5 | `docs/agents/landing-work.md` routes a merge through `tools/land.sh`, and the push that script makes passes `.githooks/pre-push`. preflight.sh is the workstation report AGENTS.md calls "what this machine is missing". land.sh names it in one refusal ("gh is not on PATH. tools/preflight.sh says what is missing.") and never runs it. preflight reports gh as `info` "(optional)" and python3 not at all, though land.sh refuses without either. | "The scripts the landing path depends on" is the wrong grouping. And the one pointer from land.sh to preflight answers "Day one: ready" on exactly the machine it sent the reader to diagnose. |
| F6 | Six shell scripts are tracked: `tools/land.sh`, `tools/preflight.sh`, `tools/smoke.sh` and `.githooks/pre-push` (integrator), `tools/scripts/certs.sh` (workstream 1) and `spikes/g0/cognito/outputs-from-terraform.sh` (workstream 3). smoke.sh is itself G1a's exit test, run by CI on every push. pre-push runs on every push, and its rules live in Python checks that have tests. | Of the integrator's four, land.sh and preflight.sh are the two nothing exercises. |
| F7 | The family: `.github/steering_tests.py`, `.github/path_ownership_tests.py`, `.github/plan-secrecy-tests.py` and `tools/roadmap_tests.py`. Each is stdlib `unittest`, run as `python3 -I` in the CI step beside its check. The first two build throwaway git repositories through `subprocess` and set only `user.name` and `user.email`. All four inherit the caller's environment. This Mac's global git configuration holds no signing, hooks path or maintenance setting. Ticket 71 covers such a repository's cleanup racing git's detached housekeeping; it was claimed while this grilling ran. | Python already does what the land.sh cases need: build a repository, start a process, read its status and output. The family's isolation from the caller is circumstance, not design. |
| F8 | This Mac's `python3` is `/usr/bin/python3`, 3.9.6. `/usr/bin/python3` and `/usr/bin/git` are the same developer-tools shim, one binary resolved into the active developer directory. The family stays 3.9-compatible through `from __future__ import annotations`. Probed: under `-I`, Python 3.9.6 leaves the script's own directory off `sys.path`. python3 is required by `.githooks/pre-push`, CI, land.sh (line 58) and `tools/roadmap.py`, yet `docs/dependencies.md` lists no Python. | "No new tool" holds wherever git is. The record that would make that checkable is missing. |
| F9 | `bash` on this Mac's PATH is `/bin/bash`, 3.2.57. Probed: under `set -u`, bash 3.2 rejects `"${args[@]}"` of an empty array as "unbound variable", where bash 4.4 and later expand it to nothing. land.sh's `merge_args=(--merge)` is never empty today. Its shebang is `#!/usr/bin/env bash`, so a Mac with Homebrew's bash first on PATH runs 5.x. | 3.2 is the floor, not the only bash that runs the scripts, and only a 3.2 run can show a 3.2-only failure. |
| F10 | From `actions/runner-images`, read 16 October 2026. `macos-latest` is macOS 26 Arm64 (image 20260907.0351.1), listing Bash 3.2.57, Python 3.14.7 and GitHub CLI 2.100.0. `ubuntu-latest` is Ubuntu 24.04 (image 20261012.334.1), listing Bash 5.2.21, Python 3.12.3 and GitHub CLI 2.102.0. | CI's one runner can never show a 3.2-only failure, and both runner images carry a real gh a test could reach. |
| F11 | The repository is public (`gh repo view`). GitHub's billing page says "GitHub Actions usage is free for self-hosted runners and for public repositories that use standard GitHub-hosted runners". It lists $0.062 a minute for the standard macOS runner on private repositories. CI ran 39 times on 15 October and 51 times on 16 October (UTC). | A macOS job costs queue time, not money, while the repository stays public. |
| F12 | Homebrew's bats-core formula is 1.14.0, MIT, with no declared runtime dependencies. Neither bats nor shellcheck is installed here. land.sh is top-level code with one function, `die`, so bats would also run it as a subprocess (`run bash tools/land.sh ...`). | bats' distinctive strength, sourcing a script's functions, has nothing to source here. |
| F13 | Probed: a `gh` double and a `git` double, each on PATH from this session's scratch directory, both ran. The refusal the ticket notes did not reproduce here. | The design should not need a git double either way. |
| F14 | cross-reference-checks ticket 07 (ready-for-agent) makes `--body-file` accept a FIFO and `/dev/fd/N` and refuse `-`. Its last checkbox reads "Verified under the bash 3.2 macOS ships, by running the script's own parse loop". Ticket 70 (ready-for-human) offers "a check in `tools/land.sh`" as its first option. | Both change land.sh's pre-merge behaviour, and 07 prescribes the method this ticket exists to retire. |
| F15 | "Harness" in this repository already has two meanings. One is task 6.1's gateway test harness (`tests/Gateway.TestHarness`; "the harness" throughout the functional and negative suite specs). The other is a G0 feasibility harness (GLOSSARY.md, Evidence ids: "the token-and-proxy harness"). "Hermetic (H)" is the gateway's test tier. The family's word for a check's tests is "test companion" (ticket 66, cross-reference-checks ticket 01). AGENTS.md and GLOSSARY.md (Integrator) say "doubles". | The title's term collides with both existing meanings, and "stubbed" is not the repository's word. |
| F16 | CODING_STANDARDS.md asks a new invariant test to name the mutation that made it fail. It forbids a test that skips on the machine it runs on ("fail and name it"), and says a guard reads the thing it guards. `.githooks/pre-push` says "The tests stay in CI". `docs/dependencies.md` records D-1 there "and not as an ADR", because shipping code touches the choice in one file. | Shapes Q10, Q16, Q17 and Q18, and sets the precedent for where D-3 goes. |

### Round 1 — the frame

❓ **Q1** - **Which tool runs the tests?**: Three options, as the ticket lists them.

- **bats-core 1.14.0** (MIT, F12). The usual choice. It is a new tool in CI and on every machine,
  and assertions need `bats-support` and `bats-assert` as well. land.sh has nothing to source, so
  bats would start it as a subprocess too: the third option's shape, in a second language.
- **Plain bash test scripts.** No tool and no assertion library. Every file rebuilds setup, the gh
  double, the throwaway repositories and the reporting, all in bash 3.2.
- **Python's `unittest`, starting the real script as a subprocess.** Standard library only.
  python3 already runs the hook, CI, land.sh and the roadmap (F8). The family already builds
  throwaway git repositories and asserts on a process's status and output (F7). On macOS, python3
  comes with the same developer tools as git (F8).

➡️ Python's `unittest`, run as `python3 -I`, with each case starting the tracked script in a
subprocess. It adds no tool, it has the family's shape, and it runs wherever the checks already run.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q2** - **What does this ticket cover?**: The Done-when names land.sh's argument parsing and its
pre-merge refusals. But the defect the Problem cites, 2c9962f, is not a refusal: it shows only in
what gh receives at the merge call. F3's two holes show there too. The Problem also leads with
preflight.sh, whose test has a different shape (F4). And land.sh's post-merge path (fast-forward,
roadmap, refresh commit, push) is what the script exists for, and it is untested too. Options:

- (a) land.sh's parsing, its pre-merge refusals and the arguments of its merge call, with
  preflight.sh and the post-merge path filed as follow-ups;
- (b) (a) plus preflight's parse agreement;
- (c) every integrator script.

➡️ (a). The merge call's arguments join the Done-when, because that is where 2c9962f shows.
preflight.sh and the post-merge path become their own tickets (Q15), so this one stays one script
and one mechanism.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q3** - **Where does bash 3.2 actually run?**: "Must run under the bash 3.2 macOS ships" reads two
ways: the tests can be run on a Mac, or the scripts run under bash 3.2 while they are tested. Only
the second catches a bash-4 construct, and CI's only runner has bash 5.2.21 (F10). The hook keeps
tests in CI (F16), so nobody runs them locally as a habit. Options:

- (a) a macOS job runs the same tests under `/bin/bash` 3.2.57, beside the Linux step;
- (b) Linux only, with 3.2 left to whoever runs the tests on a Mac;
- (c) build bash 3.2 on the Linux runner.

➡️ (a). It is the shell both scripts' headers name, and it is free while the repository is public
(F11). It is also the only run of 3.2 that nobody has to remember. The scenario it catches: a
refactor to `merge_args=()` with conditional appends passes Linux CI and fails on every Mac with
"unbound variable" (F9). Linux stays as well, because bash 5 also runs the script, through the
shebang (F9).

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q4** - **What gets a double?**: Every git call land.sh makes works offline against a local bare
repository. gh is the one call that reaches GitHub (F1). Options:

- (a) real git in a throwaway repository with a bare `origin`, and a double for gh;
- (b) a git double as well;
- (c) a clone of the live repository per case.

➡️ (a). The shallow, fetch, HEAD and dirty-tree refusals are then tested against git itself, not
against an imitation of git that can drift the way a copied parse loop does. The sandbox refusal the
ticket notes stops mattering (F13). (c) would tie every case to the live tree.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q5** - **What counts as "running the script itself"?**: The Done-when's phrase needs a
boundary. Proposed:

- the tracked file's current bytes, run whole, through the interface any caller has: arguments,
  standard input, the working directory, PATH and the environment;
- when a script reads a file from beside itself (preflight.sh, F4), a whole-file copy made at test
  time counts;
- an extracted or retyped fragment never counts;
- no switch is added to a script only so a test can reach it.

➡️ That definition, written into the test companion's docstring, where the family records its
approach. It rules out how 2c9962f was verified, with a parse loop copied into a scratch script. It
also rules out a test seam such as an environment override for preflight's env file, which would
give that guard a second way to read the wrong file.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q6** - **Do pending changes to land.sh's refusals wait for this ticket?**: cross-reference-checks
ticket 07 edits the `--body-file` checks, on the lines 2c9962f fixed. Its verification is "running
the script's own parse loop" (F14), the method this ticket retires. Ticket 70's first option would
add a refusal and a gh call to land.sh.

➡️ Yes.

- If 07 has not landed when these answers are applied, it becomes blocked by overnight ticket 69.
  Its last checkbox is replaced by three cases in `tools/land_tests.py`: a readable FIFO made with
  `os.mkfifo`, a `/dev/fd/N` handed down with `pass_fds`, and `-` refused with its reason. Python
  makes both inputs without bash's process substitution.
- If 07 has landed by then, those three cases join Q16's list instead.
- If ticket 70 chooses the land.sh check, that check lands after this ticket, with its own case.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q7** - **Fix F3's two holes in this ticket's branch?**: Both are in line 47, the line 2c9962f
added. Options:

- (a) fix them here, each after the case that shows it red;
- (b) file a separate ticket and leave the cases out;
- (c) ship the cases marked as expected failures.

➡️ (a). Line 47 becomes
`body_file="$(CDPATH= cd -- "$(dirname -- "$2")" && pwd)/$(basename -- "$2")"`, in its own commit,
after the cases that show an exported `CDPATH` and a `-x.md` name red. These are 2c9962f's defect
class. A first test companion that leaves them out, or ships them red, proves less than one that
turns them green. preflight.sh line 116 has the same idiom, and goes with whichever ticket tests
preflight.sh.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q8** - **Make end of input at the subject prompt a refusal that says why?**: Per F2, a session
with no terminal that omits `--subject` gets status 1 and no reason. Options:

- (a) `read -r reply || reply=""`, so end of input reads as "no";
- (b) refuse up front when standard input is not a terminal.

➡️ (a), in its own commit, after the case that shows it red. "stopped. Pass --subject ..." then
prints, and a `y` piped in still confirms. (b) would break that pipe for no gain.

Answer: yes, pre-accepted by Adam, 2026-10-15

### Round 2 — adopting Q1 to Q8: the shape of the companion

Settled so far:

- Python's `unittest`;
- land.sh's parsing, its pre-merge refusals and its merge call;
- a macOS job beside Linux;
- real git with a gh double;
- Q5's definition of "the script itself";
- pending land.sh changes after this ticket;
- line 47 and the prompt fixed, each shown red first.

The frontier is now everything that hangs off those answers.

❓ **Q9** - **Where does the companion live, and in what shape?**: Options are beside the script,
under `.github/` with the checks, or in a new test directory.

➡️ `tools/land_tests.py`, beside `tools/land.sh`, as `tools/roadmap_tests.py` sits beside
`tools/roadmap.py`.

- It uses stdlib `unittest` and runs as `python3 -I tools/land_tests.py`.
- It stays Python 3.9-compatible, with `from __future__ import annotations` and nothing newer,
  because 3.9.6 is this workstation's python3 (F8).
- It has no shared module of its own. `-I` keeps the script's directory off `sys.path` (F8), so
  sharing would mean loading a file by path, and no second script test exists yet to share with.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q10** - **Which bash runs a case?**: Options are the shebang, a fixed `/bin/bash`, or an
interpreter chosen per run.

➡️ A fixed `/bin/bash`:

- Every case runs `/bin/bash` with the absolute path of `tools/land.sh`, naming the interpreter
  rather than trusting the shebang. On a Mac with Homebrew's bash first on PATH, the shebang would
  pick 5.x (F9).
- The companion prints that interpreter's version before its first case.
- A variable only the macOS job sets (for instance `SCRIPT_TESTS_REQUIRE_BASH=3.2`) makes the
  companion fail, not skip, when the version differs. An image change then cannot quietly turn that
  job into a bash 5 run (F16).
- One case runs the file through its shebang, so the shebang line and the executable bit are under
  test too.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q11** - **What environment does a case's process get?**: Options are the caller's environment,
as the family uses today (F7), or a constructed one.

➡️ A constructed one, inheriting nothing from the caller:

- PATH starting at the case's own bin directory;
- HOME inside the throwaway directory;
- `GIT_CONFIG_NOSYSTEM=1`;
- `GIT_CONFIG_GLOBAL` pointing at an empty file;
- `LC_ALL=C`.

A case that needs a variable sets it on purpose, as Q7's `CDPATH` case does. Two reasons. `GH_REPO`
exported in a shell, together with a real gh reachable on the case's PATH, is the one route from a
test to a real merge, and both runner images carry a real gh (F10). And `GIT_DIR` or `CDPATH` would
change what git or `cd` does.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q12** - **What do the doubles do?**: A double can answer whatever it is asked, or only the
calls land.sh makes.

➡️ Only the calls land.sh makes. The gh double is a bash script written into each case's bin
directory, answering land.sh's three call shapes with values the case sets:

- `pr view N --json state --jq .state`;
- `pr view N --json baseRefName --jq .baseRefName`;
- `pr merge N --merge`, with or without `--subject S` and `--body-file F`.

It records every call's arguments and copies the body file's bytes at the merge call. It exits 1 on
`pr merge`, so every case ends at the merge. It records any other call and exits with a distinct
status the case fails on, so a new gh call in land.sh turns cases red instead of being answered
empty. It cites gh's manual pages for the shapes it imitates, as CODING_STANDARDS.md asks of vendor
behaviour.

python3 gets a double that fails if it is ever run, so a case proves nothing before the merge runs
Python.

The bytes are what matter: a wrong path reads other bytes or none, which is how the cases see
2c9962f and both F3 holes.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q13** - **Do the throwaway repositories turn off detached housekeeping?**: Ticket 71 suspects
git's detached housekeeping of the A-05 test's cleanup error.

➡️ Yes. Each repository, clone and bare origin runs with `gc.auto=0` and `maintenance.auto=false`.
That goes through ticket 71's shared setup if 71 has landed one, and otherwise into the companion's
own setup. The cases fetch and commit, the operations after which git may start that housekeeping,
and ticket 71 was claimed while this grilling ran (F7).

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q14** - **How is the choice recorded?**: The Done-when names `docs/dependencies.md`. D-1 is the
form a decision takes there (F16).

➡️ `docs/dependencies.md` gains two things.

Under "Decided", D-3 for the shell-script tests, in D-1's form:

- an options table: bats-core 1.14.0 (MIT), plain bash, and Python's `unittest` driving the real
  script, marked chosen;
- the reason: no new tool, since python3 is already required;
- bash 3.2.57 as the floor CI holds on macOS, with bash 5 on Linux;
- "Revisit when" the repository goes private (macOS minutes then bill, at $0.062 a minute for the
  standard runner, F11), or a script needs a test a subprocess cannot observe;
- a plain statement that CI checks the Python floor for none of the family;
- its sources: Homebrew's bats-core formula, GitHub's Actions billing page and the two runner-image
  READMEs, read 16 October 2026.

Under "Needed on day one", a row: Python 3.9 or later, 3.9.6 with macOS's developer tools,
`xcode-select --install`, needed by the checks, their test companions, `tools/land.sh` and
`tools/roadmap.py`.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q15** - **Which follow-ups are filed?**: Q2 moved two pieces of work out of this ticket.

➡️ Two tickets in the overnight tracker, each blocked by this one, filed when these answers are
applied. Neither gets a number until it is filed, because the steering check fails a citation that
resolves to nothing.

1. **preflight.sh's test companion.**
   - It runs a whole-file copy of the script beside a crafted `aws-project.env` (Q5).
   - It uses an `aws` double, and a PATH holding no `curl`, so nothing reaches the network.
   - It compares each parse form that 7a364fe, eb39fec and 9ec9aa1 list with what `set -a; .`
     yields under the same bash.
   - It covers the nine guard cases 7a364fe lists, and the line-116 `CDPATH` idiom.
   - It adds the two rows preflight lacks: gh as needed rather than optional, and python3. land.sh
     refuses without either, and its gh refusal sends the reader to preflight (F5).
2. **land.sh's post-merge path:** the fast-forward refusal, the "blocks did not move" exit, and the
   refresh commit and push, tested against a bare origin.

Answer: yes, pre-accepted by Adam, 2026-10-15

### Round 3 — adopting Q9 to Q15: what the companion proves

❓ **Q16** - **Which cases, and how is the list kept complete?**: The companion needs a case list,
and a way to stop a refusal added later from going untested.

➡️ Five groups of cases.

**Refusals.** One case per `die` above line 99, each with the condition that fires it, fifteen in
all (F1).

- The stale-worktree case is the realistic one: a worktree behind `origin/main` because another
  merge landed after it was made.
- The subject prompt is answered `n` in one case and given end of input in another (Q8).
- The unreadable-file case fails and says so if it runs as root, where `[ -r ]` is always true,
  rather than skipping (F16).

**Exits with no `land:` line.** Two remain: outside a repository, and `gh pr view` failing. Their
cases assert that no merge was recorded.

**The merge call:**

- `--subject` is passed through;
- with no subject and `y` piped in, the call has no `--subject`;
- a relative body path given from a subdirectory reaches gh as the caller's bytes (2c9962f);
- the same holds under an exported `CDPATH` and for a `-x.md` name (Q7);
- a failed merge ends the run with nothing after it.

**Help.** `--help` is compared with the header read out of land.sh at test time, not with a pasted
copy, since a guard reads the thing it guards.

**Completeness.** A closing case reads land.sh, finds every `die` above the merge call, and fails
naming any that no case produced. A refusal added later without a case then fails the companion.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q17** - **What does each case assert, and what proves the assertions can fail?**: CODING_STANDARDS.md
asks a new invariant test to name the mutation that made it fail (F16).

➡️ Each case asserts three things:

- the exit status;
- for a refusal that prints one, the `land:` line on standard error, matched by the fragment that
  names the refusal ("is not a file", "shallow clone", "HEAD is not at origin/main") rather than the
  whole sentence;
- what the gh double recorded: nothing past `pr view` for a refusal, and for a merge call its exact
  arguments and body bytes.

The merge body names the mutations that turned cases red, with counts. At the least:

- line 47 reverted to its state before 2c9962f;
- one refusal deleted;
- Q7's and Q8's lines as they stood before their fixes.

A fragment pins which refusal fired, without making every rewording of a message a test edit.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q18** - **How does CI run it?**: The Done-when says "beside the Python checks", and Q3 adds a
macOS run.

➡️ Three parts.

**Linux.** The build-and-test job gains a step directly after "A-05's two rules hold", running
`python3 -I tools/land_tests.py`. It is named for the property in the family's style ("tools/land.sh
refuses before it merges, and hands gh the body its caller named"), with a comment saying why.

**macOS.** A new job on `macos-latest` runs `actions/checkout` at default depth, then the same
command with Q10's variable set to 3.2. It declares `permissions: contents: read`, since it needs no
OIDC token. It uses `macos-latest` rather than a pinned image, because this macOS 27 workstation and
the macOS 26 image both have 3.2.57, and Q10's requirement turns a change into a red job rather than
a silent one. It runs on every push, like the rest of CI. A path filter would need a second workflow
file, and is worth adding only if macOS queueing ever slows landing.

**Not the hook.** `.githooks/pre-push` does not run it, because its header keeps tests in CI.

Answer: yes, pre-accepted by Adam, 2026-10-15

### Round 4 — adopting Q16 to Q18: the ticket's own words

❓ **Q19** - **Rewrite the ticket's text to match?**: The title, the Problem and the Done-when were
written before any of the above.

➡️ Yes.

**Title:** "Test companions for the integrator's shell scripts, starting with tools/land.sh". The
file name stays, because citations go by number and the overnight workflow names the path.

**Problem:**

- it says land.sh is on the landing path and preflight.sh is not (F5);
- it replaces "five defects" with the three commits that record eight (F4);
- it says "doubles" where it now says "stubbed";
- it records that F2's silent exit and F3's two holes were found while grilling.

**Done-when** becomes:

- D-3 and the Python row are in `docs/dependencies.md` (Q14).
- `tools/land_tests.py` runs `tools/land.sh` itself (Q5) through Q16's cases and Q17's assertions,
  and fails when a refusal above the merge call has no case.
- Line 47 (Q7) and the subject prompt (Q8) are fixed in their own commits, each after the case that
  showed it red.
- CI runs the companion on Linux beside the Python checks, and on macOS under bash 3.2 (Q18).
- The two follow-ups are filed (Q15), and cross-reference-checks ticket 07 is blocked by this one or
  covered by its cases (Q6).

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q20** - **Add the proposed terms to GLOSSARY.md?**: Three terms came out of the domain notes
below.

➡️ Yes: Test companion, Harness and Landed, worded as under "Proposed glossary / ADR", in the "Work,
gates and evidence" section.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q21** - **Move the ticket to ready-for-agent once these answers are applied?**: The ticket is
ready-for-human because "it needs a tool, and a new tool is a `docs/dependencies.md` decision".

➡️ Yes. Q1 and Q14 make that decision, and every item left is mechanical.

Answer: yes, pre-accepted by Adam, 2026-10-15

### Round 5 — frontier

Empty. Every branch has been visited:

- the tool (Q1) and the scope (Q2);
- where bash 3.2 runs (Q3);
- what is real and what is a double (Q4, Q12);
- what "the script itself" means (Q5);
- sequencing against cross-reference-checks ticket 07 and ticket 70 (Q6);
- the three defects found while grilling (Q7, Q8);
- the file, the interpreter and the environment (Q9 to Q11);
- housekeeping (Q13);
- the record (Q14) and the follow-ups (Q15);
- the cases, the assertions and their mutations (Q16, Q17);
- CI (Q18);
- the ticket's words, the glossary and the status (Q19 to Q21).

Three things are left out on purpose, and each is stated rather than assumed:

- **ShellCheck.** It is a lint and a new tool, which the ticket does not ask for.
- **The Python floor in CI.** No CI job checks 3.9 for any part of the family. Q14 records that
  rather than fixing it.
- **Untracked files in the landing worktree.** They are not a refusal, because land.sh's dirty
  check reads only tracked changes. Nor can they ride in the refresh, which adds two named paths.

### Domain-modelling notes

- **"Harness" already means two things here** (F15). After Q9 there is no shared framework at all,
  only one test companion per script, so the title's word names something this ticket does not
  build. That is why Q19 changes the title and Q20 proposes an entry.
- **"Test companion" extends cleanly.** The family coined it for a check's tests (ticket 66).
  Neither land.sh nor preflight.sh is a check, but the relation is the same: a file beside the thing,
  running the real thing on inputs built to trip it.
- **"Hermetic" is reserved.** GLOSSARY.md's Hermetic (H) is the gateway's test tier, so Q11 says
  "constructed environment" and not "hermetic".
- **"Stubbed" is not the repository's word.** AGENTS.md says "against the doubles". "Test double" is
  a general concept, so it gets no entry, and the ticket simply uses the word.
- **"Pre-merge refusal", sharpened.** It means any exit of land.sh before line 99's `gh pr merge`,
  with or without a `land:` line. F2 lists three that print no such line, and Q16 covers them,
  because counting `die` lines alone would miss them. A refusal is not a rejection in GLOSSARY.md's
  sense, which is a gateway response under a rule id, so the words do not collide and "refusal"
  needs no entry.
- **"The landing path", sharpened.** It is `tools/land.sh`, then `.githooks/pre-push` on the refresh
  push the script makes. preflight.sh is not on it (F5). The states the path produces (landed,
  part-landed, done) are defined in `docs/roadmap.md` and absent from GLOSSARY.md, hence Q20's
  third entry.
- **"Must run under the bash 3.2 macOS ships" reads two ways.** The tests can be run on a Mac, or the
  scripts run under 3.2 while tested. Only the second catches anything, and only a macOS runner gives
  it to CI (F10). Because of the shebang, 3.2 is the floor and not the runtime (F9), so the companion
  runs at both ends of the range.
- **Scenario: 2c9962f replayed through Q12's double.** Run from `sub/` with `--body-file body.md`.
  The old line 47 passes `[ -f ]` in `sub/`, then the script changes to the root. The double opens
  the root's `body.md` and records no file or the wrong bytes, and the case goes red. The design
  catches the defect it was written for.
- **Scenario: an exported `CDPATH`.** Every refusal passes, and gh is handed a two-line path naming
  no file. That is the failure land.sh's own comment at lines 39 and 40 says belongs before the merge
  (F3).
- **Scenario: an agent session lands without `--subject`.** It sees the prompt, then status 1, and
  never learns why (F2).
- **Scenario: a fresh Mac without gh.** land.sh says "tools/preflight.sh says what is missing", and
  preflight answers "GitHub CLI ... not installed (optional)" and "Day one: ready" (F5).
- **Scenario: a forgotten double.** Suppose `GH_REPO` is exported and a real gh is on the case's
  PATH. An inherited environment would let `gh pr merge 5` act on the live repository's pull
  request 5. Q11 closes that.
- **Cross-check: the ticket assumes git must be stubbed.** The code says no git call land.sh makes
  leaves the machine (F1), so Q4 doubles gh alone.
- **Cross-check: "five defects".** The commits record eight (F4).

## Questions for Adam

Each is worded so "yes" accepts the recommendation. Adam accepted every recommendation in advance on
2026-10-15. The morning session applies them, so each recommendation is written out in full.

1. **Use Python's `unittest`, driving the real script as a subprocess, as the test tool?**
   *Recommendation:* stdlib `unittest`, run as `python3 -I`, with each case starting the tracked
   script in a subprocess. Not bats-core, and not a plain-bash runner.
   *Why:* no new tool, since python3 already runs the hook, CI, land.sh and the roadmap. It has the
   family's shape, and bats would run land.sh as a subprocess anyway.
   Answer: yes, pre-accepted by Adam, 2026-10-15
2. **Scope this ticket to land.sh's parsing, its pre-merge refusals and the arguments of its merge
   call, and file preflight.sh and land.sh's post-merge path as follow-ups?**
   *Recommendation:* yes. The merge call's arguments join the Done-when. preflight.sh and the
   post-merge path become their own tickets (question 15).
   *Why:* 2c9962f shows only in what gh receives, and preflight's test needs different doubles, a
   file copy and no network.
   Answer: yes, pre-accepted by Adam, 2026-10-15
3. **Run the same tests in a macOS job under `/bin/bash` 3.2.57, as well as on Linux?**
   *Recommendation:* a separate macOS job beside the Linux step, both running the same companion.
   *Why:* the Linux runner has bash 5.2.21, so it can never show a 3.2-only failure, such as an
   empty `"${a[@]}"` under `set -u` (probed). The job is free while the repository is public.
   Answer: yes, pre-accepted by Adam, 2026-10-15
4. **Test against real git in a throwaway repository with a bare `origin`, with a double for gh
   alone?**
   *Recommendation:* yes. No git double, and no clone of the live repository.
   *Why:* every git call land.sh makes works offline against a bare origin, so its refusals are
   tested against git itself, and the git-double refusal the ticket notes stops mattering.
   Answer: yes, pre-accepted by Adam, 2026-10-15
5. **Define "running the script itself" as the tracked file's current bytes, run whole through a
   caller's interface (arguments, standard input, working directory, PATH, environment), with a
   whole-file copy allowed when a script reads beside itself, no extracted fragment, and no
   test-only switch?**
   *Recommendation:* yes, written into the companion's docstring.
   *Why:* it rules out the copied parse loop 2c9962f was verified with, and a test seam that would
   give preflight's guard a second way to read the wrong file.
   Answer: yes, pre-accepted by Adam, 2026-10-15
6. **Land every pending change to land.sh's refusals after this ticket, with its cases?**
   *Recommendation:*
   - If cross-reference-checks ticket 07 has not landed, block it on overnight ticket 69 and
     replace its last checkbox with three cases in `tools/land_tests.py`: a FIFO from `os.mkfifo`,
     a `/dev/fd/N` passed with `pass_fds`, and `-` refused with its reason.
   - If 07 has landed, add those three cases to question 16's list.
   - If ticket 70 picks the land.sh check, land that check after this ticket, with its case.

   *Why:* 07 prescribes "running the script's own parse loop", the method this ticket retires, on
   the lines 2c9962f fixed.
   Answer: yes, pre-accepted by Adam, 2026-10-15
7. **Fix line 47 in this ticket's branch, test first, so an exported `CDPATH` or a name starting
   with `-` cannot defeat it?**
   *Recommendation:* line 47 becomes
   `body_file="$(CDPATH= cd -- "$(dirname -- "$2")" && pwd)/$(basename -- "$2")"`, in its own commit,
   after the cases that show both red. preflight.sh line 116's copy of the idiom goes with
   preflight's ticket.
   *Why:* probed. With `CDPATH` exported, gh receives a two-line path naming another directory, and
   `-x.md` dies in `dirname`'s option parser: 2c9962f's defect, surviving its fix.
   Answer: yes, pre-accepted by Adam, 2026-10-15
8. **Make end of input at the subject prompt print its refusal, with `read -r reply || reply=""`?**
   *Recommendation:* yes, in its own commit, after the case that shows it red. A piped `y` still
   confirms.
   *Why:* probed. Under `set -e` the failed `read` exits 1 before "stopped. Pass --subject ..."
   prints, so a session with no terminal never learns why.
   Answer: yes, pre-accepted by Adam, 2026-10-15
9. **Put the companion at `tools/land_tests.py`, stdlib `unittest`, run as `python3 -I`, Python
   3.9-compatible, with no shared module of its own?**
   *Recommendation:* yes.
   *Why:* `tools/roadmap_tests.py` is the precedent, and 3.9.6 is this Mac's python3. `-I` keeps the
   script's directory off `sys.path`, so sharing would mean loading by path, for a second consumer
   that does not exist yet.
   Answer: yes, pre-accepted by Adam, 2026-10-15
10. **Run every case under `/bin/bash`, named explicitly, print its version, make the macOS job fail
    unless it is 3.2, and run one case through the shebang?**
    *Recommendation:* yes. A variable only the macOS job sets (for instance
    `SCRIPT_TESTS_REQUIRE_BASH=3.2`) makes a mismatch a failure, not a skip.
    *Why:* the shebang picks Homebrew's bash 5 on some Macs, and an image change must not quietly
    turn the 3.2 job into a 5.x run.
    Answer: yes, pre-accepted by Adam, 2026-10-15
11. **Give each case's process a constructed environment (PATH from the case's bin directory, HOME
    in the throwaway directory, `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL` at an empty file,
    `LC_ALL=C`), with nothing inherited unless a case sets it?**
    *Recommendation:* yes.
    *Why:* `GH_REPO` in a caller's shell, plus a reachable real gh, is the one route from a test to
    a real merge, and `GIT_DIR` or `CDPATH` change what git and `cd` do.
    Answer: yes, pre-accepted by Adam, 2026-10-15
12. **Make the gh double answer only land.sh's three call shapes, record every call and the body
    file's bytes, stop every case at `pr merge` with status 1, and fail any other call; and give
    python3 a double that fails if run?**
    *Recommendation:* yes, with the doubles written in bash per case, citing gh's manual for the
    shapes they imitate.
    *Why:* the bytes are what expose a wrong path, and a double that answered anything would let a
    new gh call pass unseen.
    Answer: yes, pre-accepted by Adam, 2026-10-15
13. **Turn off detached housekeeping (`gc.auto=0`, `maintenance.auto=false`) in every throwaway
    repository, clone and bare origin, through ticket 71's setup if it has landed one?**
    *Recommendation:* yes.
    *Why:* the cases fetch and commit, the operations ticket 71 suspects in the A-05 flake, and 71
    was claimed tonight.
    Answer: yes, pre-accepted by Adam, 2026-10-15
14. **Record the choice as D-3 under "Decided" in `docs/dependencies.md`, and add a Python 3.9 or
    later row to "Needed on day one"?**
    *Recommendation:* D-3 in D-1's form, with:
    - options bats-core 1.14.0 (MIT), plain bash, and Python's `unittest` (chosen);
    - the reason, no new tool;
    - bash 3.2.57 as the floor CI holds on macOS, with bash 5 on Linux;
    - revisit when the repository goes private ($0.062 a macOS minute) or a script needs a test a
      subprocess cannot observe;
    - a plain statement that CI checks the Python floor for none of the family;
    - sources read 16 October 2026.

    Plus the row "Python 3.9 or later, 3.9.6 with macOS's developer tools,
    `xcode-select --install`".
    *Why:* the Done-when names the file, and D-3's "no new tool" rests on a python3 the file never
    mentions.
    Answer: yes, pre-accepted by Adam, 2026-10-15
15. **File two follow-ups in the overnight tracker, each blocked by this one: preflight.sh's test
    companion and land.sh's post-merge path?**
    *Recommendation:*
    1. preflight.sh's companion: a whole-file copy beside a crafted `aws-project.env`, an `aws`
       double and no `curl` on PATH. Every parse form 7a364fe, eb39fec and 9ec9aa1 list, compared
       with `set -a; .`. The nine guard cases 7a364fe lists, and line 116's `CDPATH` idiom. Rows for
       gh (needed, not optional) and for python3.
    2. land.sh after the merge: the fast-forward refusal, the "blocks did not move" exit, and the
       refresh commit and push, against a bare origin.

    Each gets a number only when it is filed.
    *Why:* question 2 keeps them out of this ticket, and an unfiled gap is the one nobody sees.
    Answer: yes, pre-accepted by Adam, 2026-10-15
16. **Cover every refusal above line 99 with a case, add the merge-call and `--help` cases, and
    close with a case that fails when a `die` above the merge has no case?**
    *Recommendation:*
    - the fifteen `die` lines, with the stale worktree "behind `origin/main`", the prompt answered
      `n` and given end of input, and the unreadable case failing as root rather than skipping;
    - the two exits with no `land:` line (outside a repository, `gh pr view` failing), each asserting
      no merge;
    - `--subject` passed through, and no subject plus `y` reaching a merge without `--subject`;
    - a relative body path from a subdirectory reaching gh as the caller's bytes, and the same under
      `CDPATH` and for `-x.md`;
    - a failed merge ending the run;
    - `--help` compared with the header read from land.sh at test time;
    - the closing completeness case.

    *Why:* land.sh exists to refuse before the merge, and the closing case makes a refusal added
    without a case fail the companion.
    Answer: yes, pre-accepted by Adam, 2026-10-15
17. **Assert each case's exit status, its `land:` line by a fragment naming the refusal, and what
    the gh double recorded, and name the mutations in the merge body?**
    *Recommendation:* yes. Name at least these mutations, each with its count: line 47 reverted to
    before 2c9962f, one refusal deleted, and Q7's and Q8's lines as they stood before their fixes.
    *Why:* CODING_STANDARDS.md asks a new invariant test to name the mutation that made it fail, and
    a fragment pins which refusal fired without making every rewording a test edit.
    Answer: yes, pre-accepted by Adam, 2026-10-15
18. **Run the companion in CI as a build-and-test step right after "A-05's two rules hold", and in a
    new `macos-latest` job with the 3.2 requirement and `permissions: contents: read`, but not in the
    pre-push hook?**
    *Recommendation:* yes. The step is named for the property ("tools/land.sh refuses before it
    merges, and hands gh the body its caller named"), with a comment saying why. The macOS job runs on
    every push, with no path filter.
    *Why:* the Done-when says beside the Python checks, the macOS job is the only 3.2 run, and the
    hook keeps tests in CI.
    Answer: yes, pre-accepted by Adam, 2026-10-15
19. **Rewrite this ticket's text to match the answers?**
    *Recommendation:*
    - Title: "Test companions for the integrator's shell scripts, starting with tools/land.sh", with
      the file name unchanged.
    - Problem: place land.sh on the landing path and preflight.sh off it. Cite 7a364fe, eb39fec and
      9ec9aa1 (eight fixes) instead of "five defects". Say "doubles". Record the defects found while
      grilling.
    - Done-when, replaced by:
      - D-3 and the Python row recorded;
      - `tools/land_tests.py` running land.sh itself through question 16's cases and question 17's
        assertions, and failing when a refusal has no case;
      - line 47 and the prompt fixed in their own commits, after their red cases;
      - CI on Linux and on macOS under bash 3.2;
      - both follow-ups filed, and cross-reference-checks ticket 07 blocked by this one or covered by
        its cases.

    *Why:* the title names a harness this design does not build, and the Problem miscounts and
    misgroups.
    Answer: yes, pre-accepted by Adam, 2026-10-15
20. **Add Test companion, Harness and Landed to GLOSSARY.md, as worded under "Proposed glossary /
    ADR"?**
    *Recommendation:* yes, in "Work, gates and evidence".
    *Why:* "harness" already means two other things here, and landed, part-landed and done are
    defined only in `docs/roadmap.md`.
    Answer: yes, pre-accepted by Adam, 2026-10-15
21. **Set this ticket's status to ready-for-agent once the answers are applied?**
    *Recommendation:* yes.
    *Why:* it was ready-for-human only for the tool decision, and everything left is mechanical.
    Answer: yes, pre-accepted by Adam, 2026-10-15

## Proposed glossary / ADR

### Proposed `GLOSSARY.md` terms

All three are absent from `GLOSSARY.md` on `origin/main` (`eb65be8`), and all three would go under
"Work, gates and evidence".

- **Test companion** — the tests that sit beside a check or a script, in a file named for it, and
  prove what it does by running the real thing on throwaway inputs built to trip each rule; never a
  copy of its parts. _Avoid_: harness, suite, for these.
- **Harness** — one of two things, and never a script's tests. Either the gateway test harness, task
  6.1's in-process host of the composed gateway that the hermetic suites drive, or a G0 feasibility
  harness under `spikes/g0/`, which probes one mechanism and records its evidence ids. Say which when
  the sentence does not. _Avoid_: harness, for a test companion.
- **Landed** — the state of a plan row whose merge subject names it, in the form
  `docs/agents/landing-work.md` fixes. A row is **part-landed** when the subject says "part of", or
  when a split of the row merged. Landed is not done: a row is done when its tests pass in CI, it
  touched only its owner's paths and it leaves no change request open. _Avoid_: "merged" for landed,
  and "landed" for done.

### ADR candidates

None. Each decision fails at least one of the three criteria, most often "hard to reverse".

- **D-3, the tool (questions 1 and 14).** It lives in one test file and one CI step, and switching
  tools rewrites that file. D-1's own reasoning records such a choice in `docs/dependencies.md`, not
  in an ADR.
- **The macOS job (questions 3 and 18).** One job, removable in one commit.
- **Real git with a gh double, the constructed environment and the doubles' contract (questions 4,
  11 and 12).** Conventions inside one test file, recorded in its docstring.
- **The two fixes (questions 7 and 8).** Behaviour pinned by cases, and nothing surprising once the
  case exists.
- **The scope split and the sequencing (questions 2, 6 and 15).** Ticket hygiene, reversible by
  filing.

## Comments

- 2026-10-17: moved to #66 under ADR-0020; this file is the archive copy.
