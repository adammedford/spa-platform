#!/usr/bin/env python3
"""Test companion for tools/land.sh: every way it stops, what it hands gh at the merge, and what it
does once the merge is in.

Owner: integrator. Decided in #66 (archived overnight ticket 69, Q1 to Q20), which built it up to
the merge; the cases past the merge are #97's. The tool choice is D-3 in docs/dependencies.md.

**What runs is the script itself.** Each case runs the tracked file's current bytes, whole, in place,
through the interface any caller has: its arguments, standard input, the working directory, PATH
and the environment. An extracted or retyped fragment never counts, and land.sh carries no switch
put there so a test could reach it. That rules out how 2c9962f was verified -- with the parse loop
copied into a scratch script, which a later edit can drift away from.

**The interpreter is named.** Every case but one runs `/bin/bash <absolute path of land.sh>`, so a
Mac with Homebrew's bash first on PATH still tests the 3.2 that macOS ships, which the script's
header targets. The one exception runs the file through its shebang, so the executable bit and the
`#!` line are under test too. The companion prints that interpreter's version before its first
case, and when SCRIPT_TESTS_REQUIRE_BASH is set (CI's macOS job sets it to 3.2) a different version
fails the run rather than skipping it: an image change must not quietly turn the 3.2 job into a
bash 5 run.

**A constructed environment, inheriting nothing.** PATH is the case's own bin directory and nothing
else; HOME is inside the case's throwaway directory; GIT_CONFIG_NOSYSTEM=1, GIT_CONFIG_GLOBAL names
an empty file, and LC_ALL=C. A case that needs another variable sets it on purpose, as the CDPATH
case does. The reason is the route to a real merge: `GH_REPO` exported in a caller's shell, with a
real gh reachable on PATH, would let a case act on a live pull request, and both CI runner images
carry a real gh. `GIT_DIR` or `CDPATH` inherited would change what git or `cd` does.

**Real git; a double for gh and for python3.** Every git call land.sh makes works offline, so each
case builds a bare `origin` and a clone of it in its throwaway directory and lets the refusals meet
git itself. gh is the one call that leaves the machine, so each case's bin holds a strict gh double:
it answers the four `pr view` shapes land.sh uses, the two `api` listings of the pull request's
comments and reviews (from the case's own fixtures), and `pr merge`, and it records every call's
arguments. Any other call is recorded and exits 97, which turns a new gh call in land.sh into a red
case instead of an empty answer. land.sh runs python3 up to three times. Before its merge it runs
tools/review_record.py beside it, and the double hands that call to the real interpreter, so every
case reads its records with the real reader (#67). After the merge it derives the roadmap and then,
for a merge into main only, deletes the branches the merge finished, and the double records each of
those calls with the directory it ran in. The merge body is an argument, so its bytes are in the
record of the merge call, and they are what expose a wrong path: a body path checked in one
directory and read in another reaches gh as other bytes or none.

**Most cases stop at the merge.** Their gh double fails `pr merge`, and their python3 double fails
the two calls after it, so none of them gets past a merge.

**The cases past the merge (#97)** run the same script with doubles as strict, past a merge that
goes through. Their gh double makes it as GitHub does: from a clone of the case's origin standing in
for GitHub's side, it puts a merge commit on the base, with the pinned head as its second parent and
the subject and body as its message, and pushes it, so land.sh's own `git fetch origin` brings it
back. The roadmap step runs the real tools/roadmap.py, which the python3 double hands to the real
interpreter. The case's clone holds copies of the five files it reads, taken from this checkout
when the case runs, with their generated blocks made current for the case's own history, so a merge
subject that lands a plan row moves the blocks and one that names no row leaves them. That was
#97's to decide. The other way was a python3 double that rewrites the two files itself: faster, and
blind to the plan, but each case would then decide whether the blocks move, so no case could show
the merge subject moving them, or land.sh deriving them from the tree the merge made. The branch
cleanup after it is the double's to answer, and it deletes nothing and prints nothing:
tools/prune_branches_tests.py runs the real cleanup, past a merge of its own.

**No detached housekeeping.** Each repository, clone and bare origin is made with `gc.auto=0` and
`maintenance.auto=false`. The cases fetch and commit, which is when git may start `gc --auto` or
`maintenance run --auto` in the background, and a background writer is a cleanup race.

**Every `die` has a case.** A refusal is any exit before the merge call, with or without a `land:`
line, or the one `die` after it, when the worktree cannot fast-forward onto the merge. The last case
to run reads land.sh and fails naming any `die` in it that no case produced, or whose message it
cannot match to a `land:` line, so a refusal added later without a case turns this companion red.
Run the whole file: a subset leaves that closing case with less to see.

Each case asserts the exit status, the `land:` line by the fragment that names the refusal (not the
whole sentence, so a rewording is not a test edit), and exactly what the gh double recorded. A case
past the merge also asserts what origin holds afterwards, the python3 calls and where each ran, and,
when the blocks moved, the refresh commit's parent, paths and subject.

**The cases run at the same time** (#107), on the threads of tools/companion_runner.py: one per
CPU, up to eight, unless SCRIPT_TESTS_JOBS names another number. That rests on each case keeping to
its own throwaway directory and its own constructed environment. No case changes the working
directory, os.environ or anything else the process shares, and a new case must not either, but for
one list: Case.run appends each run's standard error to OBSERVED, which CPython does atomically
(https://docs.python.org/3/faq/library.html#what-kinds-of-global-value-mutation-are-thread-safe),
and only the completeness case reads it. That case runs alone, once every other case has finished,
and reports last. The tools in a case's bin directory are links to wrappers written once for the
run, because on the Mac this was measured on a newly written file costs about 45 ms more the first
time it runs; the doubles are still each case's own.

Run: python3 -I tools/land_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from typing import NamedTuple

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "land.sh"
READER = ROOT / "tools" / "review_record.py"
CLEANUP = ROOT / "tools" / "prune_branches.py"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# The real tools land.sh runs, besides gh and python3, which get doubles. Each is reached from the
# case's bin directory, which is the whole of the case's PATH.
TOOLS = ("git", "dirname", "basename", "awk")

# What tools/roadmap.py reads and writes. A case that goes past the merge commits copies of these,
# taken from this checkout when it runs, so the real roadmap.py derives the blocks from that case's
# own merge subjects; tools/prune_branches_tests.py builds its cases from the same five.
ROADMAP_FILES = (
    "tools/roadmap.py",
    "README.md",
    "docs/roadmap.md",
    "docs/plan/03-workstreams-and-tasks.md",
    "docs/plan/06-phase-2-and-risks.md",
)
# The two files tools/roadmap.py writes, and so the whole of what a refresh commit may carry.
GENERATED = ["README.md", "docs/roadmap.md"]

PR = "5"
VIEW_STATE = ["pr", "view", PR, "--json", "state", "--jq", ".state"]
VIEW_BASE = ["pr", "view", PR, "--json", "baseRefName", "--jq", ".baseRefName"]
VIEW_HEAD = ["pr", "view", PR, "--json", "headRefOid", "--jq", ".headRefOid"]
VIEW_BODY = ["pr", "view", PR, "--json", "body", "--jq", ".body"]
LIST_COMMENTS = ["api", "repos/{owner}/{repo}/issues/" + PR + "/comments", "--paginate", "--slurp"]
LIST_REVIEWS = ["api", "repos/{owner}/{repo}/pulls/" + PR + "/reviews", "--paginate", "--slurp"]
# The issues that may hold a carried finding as a filed follow-up, asked for only when the clearing
# record has a `## Follow-ups` row (#573): the listing .claude/workflows/morning.js takes for its
# filedAs, with a --jq that keeps the issues whose body has a `**Raised by:**` line naming #<pr>
# and prints each one's title. Written out here, as the calls above are, so a change to what gh is
# asked is a test edit; the double answers it from the case's `filed` fixture, so the filter's
# own reading of a body is not run here.
FILED_JQ = ('.[] | select(any(.body | split("\\n")[]; startswith("**Raised by:**") and test("#'
            + PR + '([^0-9]|$)"))) | .title')
LIST_FILED = ["issue", "list", "--state", "all", "--limit", "500", "--search",
              f"Raised by #{PR} in:body", "--json", "number,title,body", "--jq", FILED_JQ]
# What land.sh asks gh before it reads the review records, and with them, in order.
READS_HEAD = [VIEW_STATE, VIEW_BASE, VIEW_HEAD, VIEW_BODY]
READS_RECORDS = READS_HEAD + [LIST_COMMENTS, LIST_REVIEWS]
READS_FILED = READS_RECORDS + [LIST_FILED]

# The pull request's head as the double reports it, an earlier head a review once read, the base a
# record names, and a head the pull request moves to while land.sh runs.
HEAD = "1a954628a960aaef81d7b2d4521929579f3541e6"
EARLIER = "f12e996adf405952c9666956e73952681bc053d8"
BASE_SHA = "1405df66cbe219b0bf6355bc3d60361a8376b6b4"
MOVED = "d0941e68da8f38151ff86a61fc59f7c5cf9fcaa2"
MERGE = ["pr", "merge", PR, "--merge", "--match-head-commit", HEAD]
RECORD_URL = f"https://github.com/adammedford/spa-platform/pull/{PR}#issuecomment-501"
REVIEWED = f"Reviewed: {HEAD} {RECORD_URL}"


def record_comment(head: str, verdict: str, url: str = RECORD_URL, association: str = "OWNER",
                   login: str = "adammedford") -> dict:
    """A pull request comment carrying a review record, in the shape GitHub's REST API lists it:
    https://docs.github.com/en/rest/issues/comments#list-issue-comments. The line is written out
    here rather than read from the reader, which is the thing under test."""
    return {"html_url": url, "body": f"## Standards\n\nPASS.\n\nReviewed {head} against {BASE_SHA}: {verdict}\n",
            "created_at": "2026-10-18T01:00:00Z", "user": {"login": login},
            "author_association": association}


CLEARS = [record_comment(HEAD, "merge")]

# The table .claude/workflows/review-queue.js ends a record's findings with when the review carries
# a finding rather than holding on it (docs/agents/review.md -> "Landing or holding"), written out
# here in its shape rather than read from the reader, which is under test.
FOLLOW_UP_HEAD = "| Finding | Axis | Where | First carried by |"


def carrying(head: str, verdict: str, *findings: str) -> dict:
    """A record comment whose findings end in a `## Follow-ups` table with one row per finding."""
    entry = record_comment(head, verdict)
    rows = [f"| {finding} | Standards | tools/x.py:1 | this record |" for finding in findings]
    entry["body"] += "\n".join(["", "## Follow-ups", "", FOLLOW_UP_HEAD, "| --- | --- | --- | --- |",
                                *rows, ""])
    return entry

# Every standard error a case saw, for the closing completeness case.
OBSERVED: list[str] = []

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/land_tests.py for one case. It answers only the calls
# tools/land.sh makes, none of which comes after its merge, in the shapes gh's manual gives them:
#
#   gh pr view <number> --json <field> --jq <expression>     https://cli.github.com/manual/gh_pr_view
#   gh api <endpoint> --paginate --slurp                     https://cli.github.com/manual/gh_api
#   gh issue list --state all --limit 500 --search <query> --json <fields> --jq <expression>
#                                                            https://cli.github.com/manual/gh_issue_list
#   gh pr merge <number> --merge --match-head-commit <sha> [--subject <text>] --body <text>
#                                                            https://cli.github.com/manual/gh_pr_merge
#
# gh exits 1 when a command fails: https://cli.github.com/manual/gh_help_exit-codes
log={log}
pr={pr}
state={state}
base={base}
heads={heads}
comments={comments}
reviews={reviews}
filed={filed}
view_fails={view_fails}
api_fails={api_fails}
list_fails={list_fails}
server={server}
during_merge={during_merge}
description={description}

n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"

unexpected() {{
  : > "$log/unexpected"
  printf 'gh double: unexpected call, recorded as call-%s\\n' "$n" >&2
  exit 97
}}

# A case that goes past the merge names $server, a clone of its origin standing in for GitHub's
# side, and the merge then goes through as GitHub makes one: a merge commit on the base, with the
# subject and the body as its message and the pinned head as its second parent, pushed to origin, so
# that land.sh's own `git fetch origin` brings it back. GitHub's default merge method merges "using
# the `--no-ff` option":
# https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/about-merge-methods-on-github.
# GitHub refuses a pin that is not the pull request's head, the "OID that the pull request head ref
# must match to allow merge"
# (https://docs.github.com/en/graphql/reference/input-objects#mergepullrequestinput), and this
# refuses one that is not the last of $heads. Every such case passes --subject; without one GitHub
# titles the commit itself, which this does not imitate. $during_merge runs first, when the case has
# written it.
merge_as_github() {{
  current=""
  for head in $heads; do current="$head"; done
  if [ "$1" != "$current" ]; then
    printf 'gh double: %s is not the head of #%s\\n' "$1" "$pr" >&2
    exit 1
  fi
  if [ -z "$2" ]; then
    printf 'gh double: a merge that goes through needs --subject here\\n' >&2
    unexpected
  fi
  if [ -x "$during_merge" ]; then "$during_merge" >&2 || exit 1; fi
  git -C "$server" fetch -q origin || exit 1
  git -C "$server" checkout -q --detach "origin/$base" || exit 1
  git -C "$server" merge -q --no-ff -m "$2" -m "$3" "$1" || exit 1
  git -C "$server" push -q origin "HEAD:refs/heads/$base" || exit 1
  exit 0
}}

case "$1" in
  pr)
    [ "$#" -ge 3 ] && [ "$3" = "$pr" ] || unexpected
    case "$2" in
      view)
        [ "$#" -eq 7 ] && [ "$4" = --json ] && [ "$6" = --jq ] || unexpected
        if [ -n "$view_fails" ]; then printf '%s\\n' "$view_fails" >&2; exit 1; fi
        case "$5 $7" in
          "state .state") printf '%s\\n' "$state" ;;
          "baseRefName .baseRefName") printf '%s\\n' "$base" ;;
          # The description, as `--jq .body` prints a string: its bytes and a newline.
          "body .body") /bin/cat -- "$description" ;;
          # The k-th read of the head answers the k-th of $heads, and the last one after that, so
          # a case can move the head between two reads the way a push during the run would.
          "headRefOid .headRefOid")
            k=0
            while [ -e "$log/head-$k" ]; do k=$((k + 1)); done
            : > "$log/head-$k"
            answer=""
            for head in $heads; do
              answer="$head"
              [ "$k" -gt 0 ] || break
              k=$((k - 1))
            done
            printf '%s\\n' "$answer" ;;
          *) unexpected ;;
        esac
        exit 0 ;;
      merge)
        [ "$#" -ge 6 ] && [ "$4" = --merge ] && [ "$5" = --match-head-commit ] || unexpected
        pinned="$6"
        shift 6
        subject=""
        if [ "$#" -ge 2 ] && [ "$1" = --subject ]; then subject="$2"; shift 2; fi
        [ "$#" -eq 2 ] && [ "$1" = --body ] || unexpected
        if [ -z "$server" ]; then
          printf 'gh double: the merge fails on purpose, so this case does not go past it\\n' >&2
          exit 1
        fi
        merge_as_github "$pinned" "$subject" "$2" ;;
      *) unexpected ;;
    esac ;;
  api)
    [ "$#" -eq 4 ] && [ "$3" = --paginate ] && [ "$4" = --slurp ] || unexpected
    if [ -n "$api_fails" ]; then printf '%s\\n' "$api_fails" >&2; exit 1; fi
    case "$2" in
      "repos/{{owner}}/{{repo}}/issues/$pr/comments") /bin/cat -- "$comments" ;;
      "repos/{{owner}}/{{repo}}/pulls/$pr/reviews") /bin/cat -- "$reviews" ;;
      *) unexpected ;;
    esac
    exit 0 ;;
  # The follow-ups listing (#573): the search names this pull request, and the answer is what the
  # --jq would print, one title a line, from the case's fixture. The jq expression itself is the
  # case's to assert from the recorded call, as the merge body's bytes are.
  issue)
    [ "$#" -eq 12 ] && [ "$2" = list ] && [ "$7" = --search ] && [ "${{11}}" = --jq ] || unexpected
    case "$8" in *"#$pr "*) ;; *) unexpected ;; esac
    if [ -n "$list_fails" ]; then printf '%s\\n' "$list_fails" >&2; exit 1; fi
    /bin/cat -- "$filed"
    exit 0 ;;
  *) unexpected ;;
esac
"""

PYTHON_DOUBLE = """#!/bin/bash
# A double for python3, written by tools/land_tests.py. land.sh runs python3 up to three times.
# Before its merge it runs the review record reader beside it, and that call goes to the real
# interpreter in every case, so the reader under test is the real one. After the merge it derives
# the roadmap from the repository root and then, for a merge into main only, runs the branch cleanup
# beside it, and each of those calls is recorded with the directory it ran in. Where the merge
# fails, both are unexpected: a case that reaches them has gone past a merge that should have
# stopped it. Where the merge goes through, the roadmap goes to the real interpreter, over the
# copies of its inputs the case's clone holds, and the cleanup deletes nothing and prints nothing,
# since tools/prune_branches_tests.py runs the real one. The reader's arguments after its path are
# its own to check (#488: `--pr <number> --head <head>`), and its gh calls reach the gh double.
if [ "$#" -ge 2 ] && [ "$1" = -I ] && [ "$2" = {reader} ]; then exec {python} "$@"; fi
n=0
while [ -e {log}/python3-call-$n ]; do n=$((n + 1)); done
printf '%s\\0' "$(pwd -P)" "$@" > {log}/python3-call-$n
if {past_merge}; then
  if [ "$#" -eq 2 ] && [ "$1" = -I ] && [ "$2" = tools/roadmap.py ]; then exec {python} "$@"; fi
  if [ "$#" -eq 3 ] && [ "$1" = -I ] && [ "$2" = {cleanup} ] && [ "$3" = --delete ]; then exit 0; fi
fi
: > {log}/python3-unexpected
printf 'python3 double: unexpected call, recorded as python3-call-%s\\n' "$n" >&2
exit 98
"""


class Commit(NamedTuple):
    """A commit as `git log --format=%H%x00%P%x00%B` reads it: its sha, its parents in order, and its
    message without the newline git ends it with."""
    sha: str
    parents: list
    message: str

    @property
    def subject(self) -> str:
        return self.message.split("\n", 1)[0]


def bash_version() -> str:
    """The major.minor of the bash every case runs, as that bash reports it."""
    result = subprocess.run(
        [BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
        env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True)
    return result.stdout


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"land_tests: tools/land.sh runs under {BASH} {full}", file=sys.stderr)


class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout.decode("utf-8", "surrogateescape")
        self.stderr = completed.stderr.decode("utf-8", "surrogateescape")

    def land_lines(self) -> list[str]:
        return [line for line in self.stderr.splitlines() if line.startswith("land: ")]


class Case:
    """One throwaway directory: a bare origin, a clone sitting at origin/main, and a bin directory
    holding the doubles and a way to each real tool, which is the case's whole PATH."""

    def __init__(self, test: unittest.TestCase, *, state: str = "OPEN", base: str = "main",
                 heads: tuple[str, ...] = (HEAD,), comments: list | None = None,
                 reviews: list | None = None, filed: list[str] | None = None,
                 view_fails: str = "", api_fails: str = "", list_fails: str = "",
                 description: str = "", gh: bool = True, python3: bool = True):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        # Resolved, because bash's pwd reports the physical path when the caller's PWD is not
        # inherited, and macOS's temporary directory sits behind a symbolic link.
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.log = self.dir / "log"
        self.home = self.dir / "home"
        self.gitconfig = self.dir / "gitconfig"
        for directory in (self.bin, self.log, self.home):
            directory.mkdir()
        self.gitconfig.write_bytes(b"")
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            # A link to a one-line exec of the resolved binary, written once for the run, rather
            # than a link to the binary itself: a git built with RUNTIME_PREFIX finds its helper
            # programs relative to the path it was started from
            # (https://github.com/git/git/blob/master/Makefile), and through a link that path
            # would be this directory.
            runner.link_tool(self.bin / tool, os.path.realpath(found))
        if gh:
            self.serve(state=state, base=base, heads=heads, comments=comments, reviews=reviews,
                       filed=filed, view_fails=view_fails, api_fails=api_fails,
                       list_fails=list_fails, description=description)
        if python3:
            self.write_python3(past_merge=False)

        self.origin = self.dir / "origin.git"
        self.work = self.dir / "work"
        self.git("init", "-q", "--bare", "-b", "main", str(self.origin), cwd=self.dir)
        self.quiet_housekeeping(self.origin)
        self.clone(str(self.origin), self.work)
        # Two commits, so a depth-1 clone of this origin is genuinely shallow.
        for name in ("README.md", "CHANGELOG.md"):
            (self.work / name).write_text(f"{name}\n")
            self.git("add", name, cwd=self.work)
            self.git("commit", "-q", "-m", f"Add {name}", cwd=self.work)
        self.git("push", "-q", "origin", "main", cwd=self.work)
        self.git("fetch", "-q", "origin", cwd=self.work)

    def write_executable(self, name: str, text: str):
        path = self.bin / name
        path.write_text(text)
        path.chmod(0o755)

    def serve(self, *, state: str = "OPEN", base: str = "main", heads: tuple[str, ...] = (HEAD,),
              comments: list | None = None, reviews: list | None = None,
              filed: list[str] | None = None, view_fails: str = "", api_fails: str = "",
              list_fails: str = "", server: str = "", description: str = ""):
        """Writes the gh double, the two listings, the filed titles and the description it answers
        from. Given a `server`, a clone of origin, its merge goes through; without one the merge
        fails, as every case outside PostMergeTests and ClosesTests needs. The default description
        has no Closes line, so land.sh reads no commit of the head, which in most cases git does
        not hold. `filed` is each title the follow-ups listing's --jq prints (#573), none by
        default; `list_fails` makes that listing fail with the text."""
        # One page each, as `gh api --paginate --slurp` wraps them (`gh api --help`).
        listings = {"comments": CLEARS if comments is None else comments, "reviews": reviews or []}
        for name, entries in listings.items():
            (self.dir / f"{name}.json").write_text(json.dumps([entries]))
        (self.dir / "description").write_bytes(f"{description}\n".encode())
        (self.dir / "filed").write_text("".join(f"{title}\n" for title in filed or []))
        self.write_executable("gh", GH_DOUBLE.format(
            log=shlex.quote(str(self.log)), pr=shlex.quote(PR), state=shlex.quote(state),
            base=shlex.quote(base), heads=shlex.quote(" ".join(heads)),
            comments=shlex.quote(str(self.dir / "comments.json")),
            reviews=shlex.quote(str(self.dir / "reviews.json")),
            filed=shlex.quote(str(self.dir / "filed")),
            view_fails=shlex.quote(view_fails), api_fails=shlex.quote(api_fails),
            list_fails=shlex.quote(list_fails),
            server=shlex.quote(server), during_merge=shlex.quote(str(self.dir / "during-merge")),
            description=shlex.quote(str(self.dir / "description"))))

    def write_python3(self, *, past_merge: bool):
        """Writes the python3 double, which answers the roadmap and the cleanup only `past_merge`."""
        self.write_executable("python3", PYTHON_DOUBLE.format(
            log=shlex.quote(str(self.log)), reader=shlex.quote(str(READER)),
            python=shlex.quote(os.path.realpath(sys.executable)),
            past_merge="true" if past_merge else "false", cleanup=shlex.quote(str(CLEANUP))))

    def env(self, **extra: str) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "GIT_CONFIG_NOSYSTEM": "1",
               "GIT_CONFIG_GLOBAL": str(self.gitconfig), "LC_ALL": "C"}
        env.update(extra)
        return env

    def git(self, *args: str, cwd: pathlib.Path):
        subprocess.run(["git", *args], cwd=cwd, env=self.env(), check=True, capture_output=True)

    def git_out(self, *args: str, cwd: pathlib.Path) -> str:
        return subprocess.run(["git", *args], cwd=cwd, env=self.env(), check=True,
                              capture_output=True, text=True).stdout

    def quiet_housekeeping(self, repository: pathlib.Path):
        self.git("config", "gc.auto", "0", cwd=repository)
        self.git("config", "maintenance.auto", "false", cwd=repository)

    def clone(self, source: str, target: pathlib.Path, *options: str):
        self.git("clone", "-q", "-c", "gc.auto=0", "-c", "maintenance.auto=false", *options,
                 source, str(target), cwd=self.dir)
        self.git("config", "user.name", "Land Tests", cwd=target)
        self.git("config", "user.email", "land-tests@example.invalid", cwd=target)

    def run(self, *args: str, cwd: pathlib.Path | None = None, stdin: bytes = b"",
            env: dict[str, str] | None = None, through_shebang: bool = False,
            pass_fds: tuple[int, ...] = ()) -> Result:
        command = [str(SCRIPT), *args] if through_shebang else [BASH, str(SCRIPT), *args]
        # pass_fds keeps each descriptor open in land.sh under the same number, as a caller's
        # `<(...)` does; every other descriptor above 2 is closed (close_fds is on whenever
        # pass_fds is given): https://docs.python.org/3/library/subprocess.html#subprocess.Popen
        completed = subprocess.run(command, cwd=cwd or self.work, env=env or self.env(),
                                   input=stdin, capture_output=True, timeout=60, pass_fds=pass_fds)
        result = Result(completed)
        OBSERVED.append(result.stderr)
        return result

    def calls(self) -> list[list[str]]:
        names = sorted(self.log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        return [path.read_bytes().decode("utf-8", "surrogateescape").split("\0")[:-1]
                for path in names]

    def python3_calls(self) -> list[list[str]]:
        """Each python3 call but the reader's: the directory it ran in, then its arguments."""
        names = sorted(self.log.glob("python3-call-*"),
                       key=lambda path: int(path.name.rsplit("-", 1)[1]))
        return [path.read_bytes().decode("utf-8", "surrogateescape").split("\0")[:-1]
                for path in names]


class MergingCase(Case):
    """A Case whose merge goes through, for what land.sh does after it (#97).

    The clone's history ends in copies of the five files tools/roadmap.py reads, taken from this
    checkout now, and a commit that makes their generated blocks current for this history, so the
    blocks move only when the merge lands a plan row. A pull request branch with one commit of its
    own, or the `commits` a case names, sits on origin; the gh double reports its tip as the head, a
    review record clears it, and `description` is the pull request's. The branch is written in
    `author`, a clone of its own, and the clone land.sh runs in never fetches it, as a worktree made
    at origin's base holds none of a pull request's commits until land.sh fetches them (#566).
    The double's merge goes through, from `server`, a clone of origin standing in for GitHub's side.
    The clone sits detached at origin's base, as the worktree land.sh's header asks for does.
    """

    BRANCH = "change"

    def __init__(self, test: unittest.TestCase, *, base: str = "main",
                 commits: tuple[str, ...] = ("The change under review",), description: str = ""):
        super().__init__(test, base=base)
        for name in ROADMAP_FILES:
            target = self.work / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.git("add", *ROADMAP_FILES, cwd=self.work)
        self.git("commit", "-q", "-m", "Import the roadmap's inputs", cwd=self.work)
        # The copies carry this checkout's blocks, derived from its own tags and merges, so they
        # would move on the first derivation whatever merged. Made current here, they move only for
        # a merge that changes what the roadmap reads.
        subprocess.run([sys.executable, "-I", "tools/roadmap.py"], cwd=self.work, env=self.env(),
                       check=True, capture_output=True)
        self.git("commit", "-q", "--allow-empty", "-am", "Refresh the generated roadmap blocks",
                 cwd=self.work)
        self.git("push", "-q", "origin", "HEAD:refs/heads/main", cwd=self.work)
        if base != "main":
            self.git("push", "-q", "origin", f"HEAD:refs/heads/{base}", cwd=self.work)
        self.base_tip = self.worktree_head()
        self.git("fetch", "-q", "origin", cwd=self.work)
        self.git("checkout", "-q", "--detach", f"origin/{base}", cwd=self.work)

        self.author = self.dir / "author"
        self.clone(str(self.origin), self.author)
        self.git("checkout", "-q", "-b", self.BRANCH, f"origin/{base}", cwd=self.author)
        (self.author / "change.txt").write_text("the change under review\n")
        self.git("add", "change.txt", cwd=self.author)
        for message in commits:
            self.git("commit", "-q", "--allow-empty", "-m", message, cwd=self.author)

        self.server = self.dir / "server"
        self.clone(str(self.origin), self.server)
        self.base = base
        self.description = description
        self.push_branch()
        self.write_python3(past_merge=True)

    def push_branch(self):
        """Pushes the author's branch to origin and serves its tip as the head, which a review
        record clears."""
        self.head = self.git_out("rev-parse", "HEAD", cwd=self.author).strip()
        self.git("push", "-q", "-f", "origin", self.BRANCH, cwd=self.author)
        self.serve(base=self.base, heads=(self.head,), comments=[record_comment(self.head, "merge")],
                   server=str(self.server), description=self.description)

    def during_merge(self, script: str):
        """Has the gh double run `script` under /bin/sh while it merges, before the merge commit is
        made: how a case changes something while land.sh waits on gh."""
        hook = self.dir / "during-merge"
        hook.write_text(f"#!/bin/sh\n{script}\n")
        hook.chmod(0o755)

    def on_origin(self, rev: str) -> Commit:
        sha, parents, message = self.git_out("log", "-1", "--format=%H%x00%P%x00%B", rev,
                                             cwd=self.origin).split("\0")
        return Commit(sha, parents.split(), message.rstrip("\n"))

    def origin_refs(self) -> dict[str, str]:
        """{branch: tip} for every branch origin holds."""
        listed = self.git_out("for-each-ref", "--format=%(refname:strip=2) %(objectname)",
                              "refs/heads", cwd=self.origin)
        return dict(line.split(" ", 1) for line in listed.splitlines())

    def paths(self, sha: str) -> list[str]:
        """What a commit with one parent changes, against that parent."""
        return self.git_out("diff-tree", "--no-commit-id", "--name-only", "-r", sha,
                            cwd=self.origin).splitlines()

    def status(self) -> str:
        return self.git_out("status", "--porcelain", cwd=self.work).strip()

    def worktree_head(self) -> str:
        return self.git_out("rev-parse", "HEAD", cwd=self.work).strip()


class LandTestCase(unittest.TestCase):
    def assertNoStrayCall(self, case: Case):
        """No gh call the double does not answer, and no python3 call but the reader's: the others
        run only once a merge has gone through."""
        self.assertFalse((case.log / "unexpected").exists(),
                         f"land.sh made a gh call the double does not answer: {case.calls()}")
        self.assertEqual([], case.python3_calls(),
                         "land.sh made a python3 call other than the record reader's")

    def assertRefused(self, case: Case, result: Result, fragment: str, calls: list[list[str]]):
        """A refusal: status 1, a `land:` line naming it, and gh asked exactly `calls`."""
        self.assertEqual(1, result.status, result.stderr)
        self.assertTrue(any(fragment in line for line in result.land_lines()),
                        f"no land: line containing {fragment!r} in:\n{result.stderr}")
        self.assertEqual(calls, case.calls())
        self.assertNoStrayCall(case)

    def feed_fifo(self, path: pathlib.Path, data: bytes):
        """Makes a named pipe at `path` and writes `data` into it from a thread once land.sh opens
        it to read. Opening a FIFO to write blocks until a reader opens it (POSIX open(),
        https://pubs.opengroup.org/onlinepubs/9799919799/functions/open.html), which a case that
        never reads would leave blocked, so the cleanup opens it to read without blocking first."""
        os.mkfifo(path)
        def write():
            with open(path, "wb") as pipe:
                pipe.write(data)
        writer = threading.Thread(target=write, daemon=True)
        writer.start()
        def release():
            if writer.is_alive():
                reader = os.open(path, os.O_RDONLY | os.O_NONBLOCK)
                writer.join(10)
                os.close(reader)
        self.addCleanup(release)
        return writer


class RefusalTests(LandTestCase):
    """One case per `die` above the merge call, each with the condition that fires it."""

    def test_subject_without_its_value(self):
        case = Case(self)
        self.assertRefused(case, case.run(PR, "--subject"), "--subject needs a value", [])

    def test_body_file_without_its_value(self):
        case = Case(self)
        self.assertRefused(case, case.run(PR, "--body-file"), "--body-file needs a value", [])

    def test_body_file_that_is_missing_or_a_directory(self):
        for name, make in (("missing.md", None), ("a-directory", pathlib.Path.mkdir)):
            with self.subTest(name=name):
                case = Case(self)
                if make:
                    make(case.work / name)
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", name)
                self.assertRefused(case, result, "is not a file", [])

    def test_body_file_naming_a_descriptor_the_caller_did_not_pass(self):
        # The descriptor is the caller's: /dev/fd/N names whatever land.sh itself holds as N, so a
        # path whose descriptor was not inherited names nothing, the way a missing path does.
        read_end, write_end = os.pipe()
        self.addCleanup(os.close, read_end)
        self.addCleanup(os.close, write_end)
        case = Case(self)
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", f"/dev/fd/{read_end}")
        self.assertRefused(case, result, "is not a file", [])

    def test_body_file_that_is_standard_input(self):
        # gh reads `--body-file -` as standard input, but land.sh asks its no-subject confirmation
        # there, so it refuses standard input under any of its names, with the reason.
        for name in ("-", "/dev/stdin", "/dev/fd/0"):
            with self.subTest(name=name):
                case = Case(self)
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", name,
                                  stdin=b"a body on standard input\n")
                self.assertRefused(case, result, f"--body-file: {name} is standard input", [])
                self.assertIn("confirmation", result.land_lines()[0])

    def test_body_file_that_cannot_be_read(self):
        # [ -r ] is true for root whatever the mode, so as root this case cannot show the refusal.
        # It fails and says so rather than skipping: a skip here would pass having checked nothing.
        self.assertNotEqual(0, os.geteuid(),
                            "running as root, where [ -r ] is always true; run as an ordinary user")
        case = Case(self)
        body = case.work / "body.md"
        body.write_bytes(b"unreadable\n")
        body.chmod(0)
        self.addCleanup(body.chmod, 0o644)
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
        self.assertRefused(case, result, "is not readable", [])

    def test_unknown_option(self):
        case = Case(self)
        self.assertRefused(case, case.run(PR, "--admin"), "unknown option --admin", [])

    def test_two_pull_requests(self):
        case = Case(self)
        self.assertRefused(case, case.run(PR, "6"), "one pull request at a time", [])

    def test_no_pull_request_number(self):
        case = Case(self)
        self.assertRefused(case, case.run(), "which pull request?", [])

    def test_gh_missing_from_path(self):
        case = Case(self, gh=False)
        self.assertRefused(case, case.run(PR), "gh is not on PATH", [])

    def test_python3_missing_from_path(self):
        case = Case(self, python3=False)
        self.assertRefused(case, case.run(PR), "python3 is not on PATH", [])

    def test_shallow_clone(self):
        case = Case(self)
        shallow = case.dir / "shallow"
        # A file:// URL, because git ignores --depth when cloning a plain local path.
        case.clone(case.origin.as_uri(), shallow, "--depth", "1")
        result = case.run(PR, "--subject", "Merge task 9.9: x", cwd=shallow)
        self.assertRefused(case, result, "shallow clone", [])

    def test_pull_request_not_open(self):
        case = Case(self, state="MERGED")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertRefused(case, result, f"pull request #{PR} is MERGED", [VIEW_STATE])

    def test_base_that_cannot_be_fetched(self):
        case = Case(self, base="gone")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertRefused(case, result, "could not fetch origin/gone", [VIEW_STATE, VIEW_BASE])

    def test_worktree_behind_its_origin(self):
        # The realistic way to be stale: another merge landed on origin after this worktree was made.
        case = Case(self)
        other = case.dir / "other"
        case.clone(str(case.origin), other)
        (other / "LATER.md").write_text("landed meanwhile\n")
        case.git("add", "LATER.md", cwd=other)
        case.git("commit", "-q", "-m", "Merge task 9.8: landed meanwhile", cwd=other)
        case.git("push", "-q", "origin", "main", cwd=other)
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertRefused(case, result, "HEAD is not at origin/main", [VIEW_STATE, VIEW_BASE])

    def test_uncommitted_changes(self):
        for staged in (False, True):
            with self.subTest(staged=staged):
                case = Case(self)
                (case.work / "README.md").write_text("edited\n")
                if staged:
                    case.git("add", "README.md", cwd=case.work)
                result = case.run(PR, "--subject", "Merge task 9.9: x")
                self.assertRefused(case, result, "uncommitted changes", [VIEW_STATE, VIEW_BASE])

    def test_subject_prompt_answered_no(self):
        case = Case(self)
        result = case.run(PR, stdin=b"n\n")
        self.assertIn("Continue? [y/N]", result.stdout)
        self.assertRefused(case, result, "stopped. Pass --subject", READS_RECORDS)

    def test_subject_prompt_given_end_of_input(self):
        # A session with no terminal that leaves out --subject. It must learn why it stopped.
        case = Case(self)
        result = case.run(PR, stdin=b"")
        self.assertIn("Continue? [y/N]", result.stdout)
        self.assertRefused(case, result, "stopped. Pass --subject", READS_RECORDS)

    def test_unreviewed_without_its_reason(self):
        case = Case(self)
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--unreviewed")
        self.assertRefused(case, result, "--unreviewed needs a reason", [])

    def test_unreviewed_with_a_blank_reason(self):
        for reason in ("", "   ", "\t"):
            with self.subTest(reason=reason):
                case = Case(self)
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--unreviewed", reason)
                self.assertRefused(case, result, "the reason is blank", [])

    def test_unreviewed_with_a_reason_over_two_lines(self):
        # The merge body carries the reason as one `Unreviewed:` line, and a second line would
        # start something the trailer does not say.
        case = Case(self)
        result = case.run(PR, "--subject", "Merge task 9.9: x",
                          "--unreviewed", "main is red\nReviewed: nothing")
        self.assertRefused(case, result, "the reason must be one line", [])

    def test_head_that_is_not_a_full_sha(self):
        # Pinned to an empty or abbreviated head, the merge would not be pinned to the head the
        # records were read against, so it stops before reading them.
        for head in ("", HEAD[:7]):
            with self.subTest(head=head):
                case = Case(self, heads=(head,) if head else ())
                result = case.run(PR, "--subject", "Merge task 9.9: x")
                self.assertRefused(case, result, "which is not a full commit sha",
                                   [VIEW_STATE, VIEW_BASE, VIEW_HEAD])


class ReviewRecordRefusalTests(LandTestCase):
    """The real land.sh, with the real reader, refusing a head no review record clears (#67).
    tools/review_record_tests.py holds every rule of the reader; these show each refusal stopping
    the script itself before its merge."""

    def assertNotCleared(self, case: Case, result: Result, *reader_says: str):
        self.assertRefused(case, result, f"no review record clears #{PR} at {HEAD}", READS_RECORDS)
        for fragment in reader_says:
            self.assertIn(fragment, result.stderr)

    def test_no_record(self):
        case = Case(self, comments=[])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertNotCleared(case, result, f"no review record names {HEAD}")

    def test_the_latest_record_of_the_head_holds(self):
        case = Case(self, comments=[record_comment(HEAD, "hold")])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertNotCleared(case, result, "says hold", RECORD_URL)

    def test_a_merge_record_of_an_earlier_head(self):
        # A review covers the head it read; a commit pushed after it is not covered.
        case = Case(self, comments=[record_comment(EARLIER, "merge")])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertNotCleared(case, result, f"no review record names {HEAD}", EARLIER)

    def test_a_record_not_in_its_form(self):
        entry = record_comment(HEAD, "merge")
        entry["body"] = f"Reviewed {HEAD[:7]} against {BASE_SHA[:7]}: merge"
        case = Case(self, comments=[entry])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertNotCleared(case, result, "not in the record's form")

    def test_a_record_from_outside_the_repository(self):
        # The repository is public: any signed-in GitHub user can comment the line on a pull request.
        case = Case(self, comments=[record_comment(HEAD, "merge", association="NONE",
                                                   login="a-stranger")])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertNotCleared(case, result, f"no review record names {HEAD}",
                              "posted by a-stranger, whose author_association is NONE")

    def test_a_record_in_a_commented_review_clears_as_a_comment_does(self):
        review = {"html_url": f"https://github.com/adammedford/spa-platform/pull/{PR}#pullrequestreview-7",
                  "body": f"Reviewed {HEAD} against {BASE_SHA}: merge", "state": "COMMENTED",
                  "submitted_at": "2026-10-18T01:00:00Z", "commit_id": HEAD,
                  "user": {"login": "adammedford"}, "author_association": "OWNER"}
        case = Case(self, comments=[], reviews=[review])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("the merge fails on purpose", result.stderr)
        self.assertEqual(f"Reviewed: {HEAD} {review['html_url']}", case.calls()[-1][-1])

    def test_a_listing_gh_cannot_fetch(self):
        # The reader makes the listing calls since #488, so gh's failure reaches land.sh as the
        # reader's status 2, and gh's own words pass through above the refusal.
        case = Case(self, api_fails="gh: Server Error (HTTP 502)")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertRefused(case, result, f"could not read the review records of #{PR}",
                           READS_HEAD + [LIST_COMMENTS])
        self.assertIn("HTTP 502", result.stderr)


class FollowUpsTests(LandTestCase):
    """A clearing record whose comment carries a `## Follow-ups` row owes the merge body an
    `Outstanding:` line (#521): docs/agents/review.md -> "Landing or holding" records a carried
    finding there. land.sh reads the rows with the reader's --follow-ups and refuses, before the
    merge, a body with no such line, naming each row. A record with no table, and a landing made
    with --unreviewed, are not checked. The merges here fail on purpose at gh, as MergeCallTests'
    do, which is what shows the check let them through.

    A row with the line still owes an issue (#573): the morning files a record's rows from open
    pull requests only (#514), so before the merge land.sh lists the issues raised by the pull
    request and names each row no issue answers, matched as morning.js's filedAs matches, the
    finding as the title once lowercased and its whitespace collapsed. It warns and merges; a row
    an issue answers, and a record with no row, print nothing, and a listing gh cannot make is
    said and not held against the merge."""

    FINDING = "AppDefinitionValidationTests cannot fail on an empty host"
    OTHER_FINDING = "TraceTests reads the span name from the test's own string"
    WITHOUT = "Verified by tools/land_tests.py.\n\nOutstanding test ids: none"
    WITH = "Verified by tools/land_tests.py.\n\nOutstanding: the two carried findings, filed tomorrow"
    UNFILED = "no issue has filed"

    def assertMerged(self, case: Case, result: Result, body: str, before: list[list[str]] = READS_RECORDS,
                     lines: list[str] | None = None):
        """The merge was asked for after exactly `before`, and the `land:` lines, each matched by
        a fragment of `lines`, in order, were the only ones."""
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("the merge fails on purpose", result.stderr)
        self.assertEqual(before + [MERGE + ["--subject", "Merge task 9.9: x", "--body", body]],
                         case.calls())
        printed = result.land_lines()
        self.assertEqual(len(lines or []), len(printed), result.stderr)
        for fragment, line in zip(lines or [], printed):
            self.assertIn(fragment, line)
        self.assertNoStrayCall(case)

    def unfiled(self, finding: str) -> str:
        """The warning's fragment naming one row, with the finding as the record wrote it."""
        return f"{self.UNFILED}: {finding}."

    def test_an_unfiled_row_is_named_and_the_merge_goes_on(self):
        case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)])
        (case.work / "body.md").write_bytes(self.WITH.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
        self.assertMerged(case, result, f"{self.WITH}\n\n{REVIEWED}", before=READS_FILED,
                          lines=[self.unfiled(self.FINDING)])
        line = result.land_lines()[0]
        # It says what the morning will not do once the merge is made, and how the row is filed.
        self.assertIn("morning", line)
        self.assertIn(f"#{PR} merges", line)
        self.assertIn(f"**Raised by:** #{PR}", line)

    def test_a_row_a_filed_issue_answers_names_nothing(self):
        # The title is matched as morning.js's filedAs matches it: case and runs of whitespace are
        # not differences. The listing already holds only the issues whose Raised-by line names the
        # pull request, so a title alone answers a row here.
        for title in (self.FINDING, self.FINDING.upper(), f"  {self.FINDING.replace(' ', '   ')} "):
            with self.subTest(title=title):
                case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)], filed=[title])
                (case.work / "body.md").write_bytes(self.WITH.encode())
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
                self.assertMerged(case, result, f"{self.WITH}\n\n{REVIEWED}", before=READS_FILED)

    def test_only_the_rows_no_issue_answers_are_named(self):
        # Two rows, one filed and one whose title differs by a word: one warning, one line.
        case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING, self.OTHER_FINDING)],
                    filed=[self.OTHER_FINDING, self.FINDING.replace("empty", "blank")])
        (case.work / "body.md").write_bytes(self.WITH.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
        self.assertMerged(case, result, f"{self.WITH}\n\n{REVIEWED}", before=READS_FILED,
                          lines=[self.unfiled(self.FINDING)])

    def test_a_listing_gh_cannot_make_is_said_and_the_merge_goes_on(self):
        case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)],
                    list_fails="error connecting to api.github.com")
        (case.work / "body.md").write_bytes(self.WITH.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
        self.assertMerged(case, result, f"{self.WITH}\n\n{REVIEWED}", before=READS_FILED,
                          lines=["could not list the issues"])
        self.assertIn("error connecting to api.github.com", result.stderr)

    @unittest.skipUnless(shutil.which("jq"), "jq is not on this machine; the filter runs under gh's own")
    def test_the_filter_keeps_the_issues_raised_by_the_pull_request(self):
        # The filter gh is handed, run by jq over issues as `gh issue list --json` lists them: the
        # body's Raised-by line names #5 and not #50, wherever it stands, and the title comes out
        # as written. gh evaluates it with its own jq, which this stands in for.
        issues = [{"number": 1, "title": "  Filed As Written ", "body": "**Owner:** x\r\n**Raised by:** #5, record\r\n"},
                  {"number": 2, "title": "Another pull request's", "body": "**Raised by:** #50"},
                  {"number": 3, "title": "Named in prose", "body": "Raised by #5 in prose"},
                  {"number": 4, "title": "Line last, no newline", "body": "text\n**Raised by:** #5"}]
        printed = subprocess.run(["jq", "-r", FILED_JQ], input=json.dumps(issues), text=True,
                                 capture_output=True, check=True).stdout
        self.assertEqual("  Filed As Written \nLine last, no newline\n", printed)

    def test_a_body_with_no_outstanding_line_is_refused_naming_each_row(self):
        for written in (self.WITHOUT, None):
            with self.subTest(body=written):
                case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING, self.OTHER_FINDING)])
                args = [PR, "--subject", "Merge task 9.9: x"]
                if written is not None:
                    (case.work / "body.md").write_bytes(written.encode())
                    args += ["--body-file", "body.md"]
                result = case.run(*args)
                self.assertRefused(case, result, "has no Outstanding: line", READS_RECORDS)
                line = next(line for line in result.land_lines() if "has no Outstanding: line" in line)
                self.assertIn(self.FINDING, line)
                self.assertIn(self.OTHER_FINDING, line)

    def test_a_body_with_an_outstanding_line_merges(self):
        case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)], filed=[self.FINDING])
        (case.work / "body.md").write_bytes(self.WITH.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
        self.assertMerged(case, result, f"{self.WITH}\n\n{REVIEWED}", before=READS_FILED)

    def test_the_line_is_read_wherever_it_stands_in_the_body(self):
        # Indented, or first, or last with no newline after it: a line is a line.
        for written in ("  Outstanding: one finding\nThe rest.", "Outstanding: one\n", "Why.\nOutstanding: x"):
            with self.subTest(body=written):
                case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)], filed=[self.FINDING])
                (case.work / "body.md").write_bytes(written.encode())
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
                self.assertMerged(case, result, f"{written.rstrip(chr(10))}\n\n{REVIEWED}",
                                  before=READS_FILED)

    def test_a_record_with_no_table_merges_without_the_line(self):
        for comments in (CLEARS, [carrying(HEAD, "merge")]):
            with self.subTest(table="empty" if comments is not CLEARS else "none"):
                case = Case(self, comments=comments)
                (case.work / "body.md").write_bytes(self.WITHOUT.encode())
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
                self.assertMerged(case, result, f"{self.WITHOUT}\n\n{REVIEWED}")

    def test_an_unreviewed_landing_is_not_checked(self):
        case = Case(self, comments=[carrying(HEAD, "merge", self.FINDING)])
        (case.work / "body.md").write_bytes(self.WITHOUT.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md",
                          "--unreviewed", "main is red and cannot wait")
        self.assertMerged(case, result, f"{self.WITHOUT}\n\nUnreviewed: main is red and cannot wait",
                          before=READS_HEAD)


class SilentExitTests(LandTestCase):
    """Exits before the merge that print no `land:` line. Each must still merge nothing."""

    def test_outside_a_repository(self):
        case = Case(self)
        elsewhere = case.dir / "elsewhere"
        elsewhere.mkdir()
        # The ceiling stops git's upward search at the throwaway directory, wherever it sits.
        result = case.run(PR, "--subject", "Merge task 9.9: x", cwd=elsewhere,
                          env=case.env(GIT_CEILING_DIRECTORIES=str(case.dir)))
        # 128 is git's own status for a fatal error, which set -e passes on.
        self.assertEqual(128, result.status, result.stderr)
        self.assertIn("not a git repository", result.stderr)
        self.assertEqual([], case.calls())
        self.assertNoStrayCall(case)

    def test_gh_pr_view_fails(self):
        case = Case(self, view_fails="GraphQL: Could not resolve to a PullRequest with the number of 5.")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("Could not resolve to a PullRequest", result.stderr)
        self.assertEqual([VIEW_STATE], case.calls())
        self.assertNoStrayCall(case)


class MergeCallTests(LandTestCase):
    """What gh receives at the merge: the exact arguments, the head they pin and the body's bytes.
    Their double fails every merge, so each case also shows that a failed merge stops everything
    after it."""

    BODY = "Verified by tools/land_tests.py.\n\nOutstanding: none — exact bytes, no final newline"

    def assertMergeCalled(self, case: Case, result: Result, merge: list[str],
                          before: list[list[str]] = READS_RECORDS):
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("the merge fails on purpose", result.stderr)
        self.assertEqual(before + [merge], case.calls())
        # Ends with, rather than a last line: the subject prompt leaves no newline before it.
        self.assertTrue(result.stdout.endswith(f"--- merging #{PR}\n"),
                        f"land.sh printed something after its failed merge:\n{result.stdout}")
        self.assertEqual([], result.land_lines())
        self.assertNoStrayCall(case)

    def test_subject_is_passed_through(self):
        case = Case(self)
        subject = "Merge task 9.9: the subject the roadmap reads, with 'quotes' and $dollars"
        result = case.run(PR, "--subject", subject)
        self.assertNotIn("Continue?", result.stdout)
        self.assertMergeCalled(case, result, MERGE + ["--subject", subject, "--body", REVIEWED])

    def test_piped_yes_confirms_without_adding_a_subject(self):
        case = Case(self)
        result = case.run(PR, stdin=b"y\n")
        self.assertIn("Continue? [y/N]", result.stdout)
        self.assertMergeCalled(case, result, MERGE + ["--body", REVIEWED])

    def test_the_latest_record_clears_and_its_url_is_the_trailer(self):
        # A hold, then the reviewer's merge on the same head: the merge's URL rides in the body.
        later = record_comment(HEAD, "merge", url=f"{RECORD_URL}0")
        later["created_at"] = "2026-10-18T02:00:00Z"
        case = Case(self, comments=[record_comment(HEAD, "hold"), later])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertMergeCalled(case, result, MERGE + ["--subject", "Merge task 9.9: x",
                                                      "--body", f"Reviewed: {HEAD} {RECORD_URL}0"])

    def test_the_merge_is_pinned_to_the_head_the_record_cleared(self):
        # The pull request moves to another head while land.sh runs. The merge must carry the head
        # the record named, which GitHub then refuses, and not a head read again, which no record
        # clears: https://docs.github.com/en/graphql/reference/input-objects#mergepullrequestinput
        case = Case(self, heads=(HEAD, MOVED))
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertMergeCalled(case, result, MERGE + ["--subject", "Merge task 9.9: x",
                                                      "--body", REVIEWED])

    def test_unreviewed_merges_pinned_with_its_reason_and_reads_no_records(self):
        case = Case(self, comments=[])
        reason = "main is red on the roadmap check; the fix cannot wait for a session"
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--unreviewed", reason)
        self.assertMergeCalled(case, result,
                               MERGE + ["--subject", "Merge task 9.9: x", "--body", f"Unreviewed: {reason}"],
                               before=READS_HEAD)

    def test_the_trailer_follows_the_body_after_one_blank_line(self):
        for written, trailer_after in ((self.BODY, self.BODY), (self.BODY + "\n\n\n", self.BODY),
                                       ("", None)):
            with self.subTest(written=written):
                case = Case(self)
                (case.work / "body.md").write_bytes(written.encode())
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md")
                body = REVIEWED if trailer_after is None else f"{trailer_after}\n\n{REVIEWED}"
                self.assertMergeCalled(case, result, MERGE + ["--subject", "Merge task 9.9: x",
                                                              "--body", body])

    def test_unreviewed_trailer_follows_the_body(self):
        case = Case(self)
        (case.work / "body.md").write_bytes(self.BODY.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md",
                          "--unreviewed", "no session free tonight")
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body",
                     f"{self.BODY}\n\nUnreviewed: no session free tonight"],
            before=READS_HEAD)

    def test_relative_body_path_from_a_subdirectory(self):
        # 2c9962f: the path was checked from the caller's directory and read from the repository's
        # root. A decoy of the same name at the root makes reading the wrong one visible.
        case = Case(self)
        sub = case.work / "sub"
        sub.mkdir()
        (sub / "body.md").write_bytes(self.BODY.encode())
        (case.work / "body.md").write_bytes(b"the decoy at the root\n")
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "body.md", cwd=sub)
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])

    def test_relative_body_path_under_an_exported_cdpath(self):
        # With CDPATH exported, `cd sub` goes to the first CDPATH entry holding a sub/ and prints
        # it, so the path read names another directory, over two lines.
        case = Case(self)
        decoy = case.dir / "decoy"
        (decoy / "sub").mkdir(parents=True)
        (decoy / "sub" / "body.md").write_bytes(b"the decoy under CDPATH\n")
        (case.work / "sub").mkdir()
        (case.work / "sub" / "body.md").write_bytes(self.BODY.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "sub/body.md",
                          env=case.env(CDPATH=str(decoy)))
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])

    def test_body_from_a_named_pipe(self):
        # A FIFO is not a regular file, and gh pr merge --body-file reads one, so land.sh does too.
        for relative in (True, False):
            with self.subTest(relative=relative):
                case = Case(self)
                writer = self.feed_fifo(case.work / "body.fifo", self.BODY.encode())
                name = "body.fifo" if relative else str(case.work / "body.fifo")
                result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", name)
                self.assertMergeCalled(
                    case, result,
                    MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])
                writer.join(10)
                self.assertFalse(writer.is_alive(), "land.sh never opened the named pipe to read it")

    def test_body_from_an_inherited_descriptor(self):
        # What `--body-file <(...)` hands land.sh: a pipe's read end, inherited as /dev/fd/N. Run
        # from a subdirectory, so the script's move to the repository root comes between the
        # check and the read, and the bytes show the descriptor was still open when it read.
        case = Case(self)
        sub = case.work / "sub"
        sub.mkdir()
        read_end, write_end = os.pipe()
        self.addCleanup(os.close, read_end)
        with os.fdopen(write_end, "wb") as pipe:
            pipe.write(self.BODY.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", f"/dev/fd/{read_end}",
                          cwd=sub, pass_fds=(read_end,))
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])

    def test_body_file_named_with_a_leading_hyphen(self):
        # dirname and basename read a leading hyphen as an option unless `--` ends their options.
        case = Case(self)
        (case.work / "-x.md").write_bytes(self.BODY.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "-x.md")
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])


class PostMergeTests(LandTestCase):
    """What land.sh does once gh has merged (#97): it fetches the merge commit and fast-forwards onto
    it, derives the roadmap from the tree the merge made, and pushes the refresh as a commit of its
    own when the blocks moved. Each case runs the real script past a merge that goes through, so it
    also asserts what origin's base holds afterwards."""

    # A subject that lands a plan row, which moves the generated blocks, and one that names only a
    # ticket, which tools/roadmap.py reads for --json but counts in no block.
    LANDS_A_ROW = "Merge task 2.1: the registry"
    NAMES_NO_ROW = "Merge task issue-97: a ticket's change"
    BODY = "Verified by tools/land_tests.py."

    def asked(self, case: MergingCase, subject: str, body: str = "") -> list[list[str]]:
        """Everything gh is asked when a record clears the head and the merge goes through."""
        trailer = f"Reviewed: {case.head} {RECORD_URL}"
        return READS_RECORDS + [["pr", "merge", PR, "--merge", "--match-head-commit", case.head,
                                 "--subject", subject,
                                 "--body", f"{body}\n\n{trailer}" if body else trailer]]

    def roadmap_call(self, case: MergingCase) -> list[str]:
        return [str(case.work), "-I", "tools/roadmap.py"]

    def cleanup_call(self, case: MergingCase) -> list[str]:
        return [str(case.work), "-I", str(CLEANUP), "--delete"]

    def assertMergedOnOrigin(self, case: MergingCase, merge: Commit, subject: str, body: str = ""):
        """The premise: the double merged as GitHub would, the pinned head onto the base it found,
        with the subject and body land.sh handed it."""
        self.assertEqual([case.base_tip, case.head], merge.parents)
        self.assertEqual(f"{subject}\n\n{self.asked(case, subject, body)[-1][-1]}", merge.message)

    def assertPython3Calls(self, case: MergingCase, calls: list[list[str]]):
        """gh asked nothing it does not answer, and python3 ran exactly `calls` after the reader."""
        self.assertFalse((case.log / "unexpected").exists(),
                         f"land.sh made a gh call the double does not answer: {case.calls()}")
        self.assertFalse((case.log / "python3-unexpected").exists(),
                         f"land.sh made a python3 call the double does not answer: "
                         f"{case.python3_calls()}")
        self.assertEqual(calls, case.python3_calls())

    def test_blocks_that_moved_are_committed_alone_and_pushed_to_the_base(self):
        # From the root and from a subdirectory: land.sh moves to the root before it merges, and the
        # roadmap's relative path names the script only there.
        for where in (".", "docs"):
            with self.subTest(cwd=where):
                case = MergingCase(self)
                # The body file sits in the worktree, untracked, as a caller's often does: the path
                # besides the two generated files that a careless `git add` would carry.
                body = case.work / "body.md"
                body.write_text(f"{self.BODY}\n")
                result = case.run(PR, "--subject", self.LANDS_A_ROW, "--body-file", str(body),
                                  cwd=case.work / where)
                self.assertEqual(0, result.status, result.stderr)
                self.assertIn(f"land: #{PR} is in and the roadmap says so.", result.stdout)
                self.assertEqual([], result.land_lines())
                self.assertEqual(self.asked(case, self.LANDS_A_ROW, self.BODY), case.calls())
                refresh = case.on_origin("main")
                self.assertEqual(1, len(refresh.parents), "the refresh is one commit, on the merge")
                self.assertMergedOnOrigin(case, case.on_origin(refresh.parents[0]),
                                          self.LANDS_A_ROW, self.BODY)
                self.assertEqual(GENERATED, case.paths(refresh.sha))
                self.assertEqual(f"Refresh the generated roadmap blocks after landing #{PR}",
                                 refresh.subject)
                self.assertIn("\n\nGenerated by tools/roadmap.py, ", refresh.message)
                self.assertEqual({"main": refresh.sha, MergingCase.BRANCH: case.head},
                                 case.origin_refs())
                # The worktree sits at what it pushed, with the body file still untracked beside it.
                self.assertEqual(refresh.sha, case.worktree_head())
                self.assertEqual("?? body.md", case.status())
                current = subprocess.run([sys.executable, "-I", "tools/roadmap.py", "--check"],
                                         cwd=case.work, env=case.env(), capture_output=True,
                                         text=True)
                self.assertEqual(0, current.returncode, current.stdout)
                self.assertPython3Calls(case, [self.roadmap_call(case), self.cleanup_call(case)])

    def test_blocks_that_did_not_move_end_the_landing_with_no_commit_and_no_push(self):
        case = MergingCase(self)
        result = case.run(PR, "--subject", self.NAMES_NO_ROW)
        self.assertEqual(0, result.status, result.stderr)
        self.assertIn("land: the generated blocks did not move", result.stdout)
        self.assertEqual([], result.land_lines())
        self.assertEqual(self.asked(case, self.NAMES_NO_ROW), case.calls())
        merge = case.on_origin("main")
        self.assertMergedOnOrigin(case, merge, self.NAMES_NO_ROW)
        self.assertEqual({"main": merge.sha, MergingCase.BRANCH: case.head}, case.origin_refs())
        # Fast-forwarded onto the merge, with nothing committed on top and nothing left changed.
        self.assertEqual(merge.sha, case.worktree_head())
        self.assertEqual("", case.status())
        self.assertPython3Calls(case, [self.roadmap_call(case), self.cleanup_call(case)])

    def test_a_worktree_that_cannot_fast_forward_onto_the_merge_is_refused(self):
        # A commit made in the landing worktree while gh merges: afterwards the worktree and the
        # fetched base have each moved where the other has not, so no fast-forward joins them. The
        # merge is in by then, so the refusal says what to do, and land.sh does nothing more.
        case = MergingCase(self)
        made = "Committed in the landing worktree while the merge ran"
        case.during_merge(f"exec git -C {shlex.quote(str(case.work))} commit -q --allow-empty "
                          f"-m {shlex.quote(made)}")
        result = case.run(PR, "--subject", self.LANDS_A_ROW)
        # assertRefused also finds no python3 call, so neither the roadmap nor the cleanup ran.
        self.assertRefused(case, result, "could not fast-forward onto origin/main",
                           self.asked(case, self.LANDS_A_ROW))
        self.assertTrue(result.stdout.endswith("--- fetching the merge commit for main\n"),
                        f"land.sh went on after the refusal:\n{result.stdout}")
        merge = case.on_origin("main")
        self.assertMergedOnOrigin(case, merge, self.LANDS_A_ROW)
        self.assertEqual({"main": merge.sha, MergingCase.BRANCH: case.head}, case.origin_refs())
        # The worktree keeps the commit made during the merge: nothing reset or merged over it.
        self.assertEqual(f"{case.base_tip}\0{made}\n",
                         case.git_out("log", "-1", "--format=%P%x00%s", "HEAD", cwd=case.work))

    def test_a_merge_into_another_base_refreshes_that_base_and_runs_no_cleanup(self):
        # The refresh goes to the base the pull request names. The cleanup follows only a merge into
        # main, where a landing finishes claim branches and overnight branches.
        case = MergingCase(self, base="release")
        result = case.run(PR, "--subject", self.LANDS_A_ROW)
        self.assertEqual(0, result.status, result.stderr)
        self.assertIn(f"land: #{PR} is in and the roadmap says so.", result.stdout)
        self.assertEqual(self.asked(case, self.LANDS_A_ROW), case.calls())
        refresh = case.on_origin("release")
        self.assertEqual(1, len(refresh.parents), "the refresh is one commit, on the merge")
        self.assertMergedOnOrigin(case, case.on_origin(refresh.parents[0]), self.LANDS_A_ROW)
        self.assertEqual(GENERATED, case.paths(refresh.sha))
        self.assertEqual({"main": case.base_tip, "release": refresh.sha,
                          MergingCase.BRANCH: case.head}, case.origin_refs())
        self.assertPython3Calls(case, [self.roadmap_call(case)])


class ClosesTests(LandTestCase):
    """The description's Closes lines against the branch's commits (#566): every #n a `Closes #n`
    line names must be in the message of a non-merge commit between the base and the head, or
    nothing merges. The cases run MergingCase, whose merge would go through and whose landing clone
    holds none of the branch's commits, so a refusal is land.sh's own and a pass shows land.sh
    fetching the head before it read its commits."""

    SUBJECT = "Merge task issue-566: a change that closes tickets"

    def assertClosesRefused(self, case: MergingCase, result: Result, *unnamed: str):
        """Refused before the records are read, naming exactly `unnamed`, in the description's
        order, with origin as it was."""
        self.assertRefused(case, result,
                           f"the description of #{PR} closes {' '.join(unnamed)}, which no non-merge",
                           READS_HEAD)
        self.assertEqual({"main": case.base_tip, MergingCase.BRANCH: case.head}, case.origin_refs())

    def test_a_ticket_no_commit_names_is_refused_reviewed_or_not(self):
        # #536 on 2026-10-29: the commit naming #352 was dropped and the Closes line stayed.
        for extra in ((), ("--unreviewed", "main is red and cannot wait")):
            with self.subTest(extra=extra):
                case = MergingCase(self, description="Lands the change.\n\nCloses #352")
                self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT, *extra),
                                         "#352")

    def test_only_the_tickets_no_commit_names_are_named(self):
        # The unnamed one in lower case, which GitHub reads as a closing keyword too.
        case = MergingCase(self, commits=("Task issue-177: the first part, for #177",
                                          "The rest\n\nTicket: #388"),
                           description="Closes #177\ncloses #352\nCloses #388")
        self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT), "#352")

    def test_a_number_is_matched_whole(self):
        # Each way round: #35 is not read inside #352, and #352 is not read as #35 and a 2.
        for closes, named in (("35", "352"), ("352", "35")):
            with self.subTest(closes=closes, named=named):
                case = MergingCase(self, commits=(f"The work for #{named}",),
                                   description=f"Closes #{closes}")
                self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT), f"#{closes}")

    def test_a_merge_naming_the_ticket_does_not_count(self):
        # A merge on the branch names #352; neither commit it joins does.
        case = MergingCase(self, description="Closes #352")
        case.git("checkout", "-q", "-b", "side", "origin/main", cwd=case.author)
        case.git("commit", "-q", "--allow-empty", "-m", "Side work", cwd=case.author)
        case.git("checkout", "-q", MergingCase.BRANCH, cwd=case.author)
        case.git("merge", "-q", "--no-ff", "-m", "Merge side: the work for #352", "side",
                 cwd=case.author)
        case.push_branch()
        self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT), "#352")

    def test_a_lane_task_commit_names_its_ticket_as_issue_n(self):
        # The overnight route (#529): a lane's task commit names its ticket only as issue-<n>, and
        # the lane's merge, which carries `Ticket: #n`, is not read (decision on #566).
        case = MergingCase(self, commits=("Task issue-479: the work, with no number sign",),
                           description="Closes #479")
        result = case.run(PR, "--subject", self.SUBJECT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([], result.land_lines())
        self.assertEqual([case.base_tip, case.head], case.on_origin("main").parents)

    def test_a_message_that_opens_with_the_issue_token_names_its_ticket(self):
        # The newest non-merge commit's message is the first text the check reads, so issue-479
        # there has nothing before it, which the start of the message has to stand for.
        case = MergingCase(self, commits=("issue-479: the work",), description="Closes #479")
        result = case.run(PR, "--subject", self.SUBJECT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([], result.land_lines())
        self.assertEqual([case.base_tip, case.head], case.on_origin("main").parents)

    def test_an_issue_token_is_matched_whole(self):
        # Each way round, as for #n: issue-35 is not read inside issue-352, nor issue-352 as
        # issue-35; and a word ending in issue, such as reissue-352, is not the token.
        for closes, message in (("35", "Task issue-352: the work"),
                                ("352", "Task issue-35: the work"),
                                ("352", "The reissue-352 of the work")):
            with self.subTest(closes=closes, message=message):
                case = MergingCase(self, commits=(message,), description=f"Closes #{closes}")
                self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT), f"#{closes}")

    def test_a_lane_merge_naming_the_ticket_does_not_count(self):
        # The overnight lane merge's shape: issue-352 in its subject, Ticket: #352 in its body, and
        # neither commit it joins names the ticket.
        case = MergingCase(self, description="Closes #352")
        case.git("checkout", "-q", "-b", "side", "origin/main", cwd=case.author)
        case.git("commit", "-q", "--allow-empty", "-m", "Side work", cwd=case.author)
        case.git("checkout", "-q", MergingCase.BRANCH, cwd=case.author)
        case.git("merge", "-q", "--no-ff", "-m", "Merge task issue-352: the work\n\nTicket: #352",
                 "side", cwd=case.author)
        case.push_branch()
        self.assertClosesRefused(case, case.run(PR, "--subject", self.SUBJECT), "#352")

    def test_a_description_whose_every_ticket_a_commit_names_merges(self):
        # Named in a subject, a body and a trailer. The description was edited on the web, so its
        # lines end in a carriage return, and its keywords are in three cases, as GitHub reads any.
        # A Ticket: line closes nothing, so the ticket it names needs no commit.
        case = MergingCase(self, commits=("Task issue-177: the first part, for #177",
                                          "The second part\n\nIt finishes what #252 asked for.",
                                          "The third part\n\nTicket: #388"),
                           description="Lands three tickets.\r\n\r\nCloses #177\r\n  closes #252\r\n"
                                       "CLOSES #388\r\nTicket: #100")
        # The premise: the landing clone does not hold the head, so land.sh fetched it.
        missing = subprocess.run(["git", "cat-file", "-e", f"{case.head}^{{commit}}"],
                                 cwd=case.work, env=case.env(), capture_output=True)
        self.assertNotEqual(0, missing.returncode, "the landing clone already holds the head")
        result = case.run(PR, "--subject", self.SUBJECT)
        self.assertEqual(0, result.status, result.stderr)
        self.assertEqual([], result.land_lines())
        self.assertEqual(READS_RECORDS + [["pr", "merge", PR, "--merge", "--match-head-commit",
                                           case.head, "--subject", self.SUBJECT, "--body",
                                           f"Reviewed: {case.head} {RECORD_URL}"]], case.calls())
        self.assertEqual([case.base_tip, case.head], case.on_origin("main").parents)

    def test_a_head_whose_commits_cannot_be_read_is_refused(self):
        # The double's head is a sha origin does not hold, so the fetch for it fails.
        case = Case(self, description="Closes #352")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertRefused(case, result, f"could not read the commits of #{PR} up to {HEAD}",
                           READS_HEAD)


class HelpTests(LandTestCase):
    def header(self) -> str:
        """land.sh's comment header, read out of the file now: a guard reads the thing it guards."""
        lines = []
        for line in SCRIPT.read_text().splitlines()[1:]:
            if not line.startswith("#"):
                break
            lines.append(re.sub(r"^# ?", "", line))
        return "".join(f"{line}\n" for line in lines)

    def test_help_prints_the_header_and_asks_gh_nothing(self):
        expected = self.header()
        self.assertGreater(len(expected.splitlines()), 1, "land.sh has no comment header to compare")
        for flag in ("--help", "-h"):
            with self.subTest(flag=flag):
                case = Case(self)
                result = case.run(flag)
                self.assertEqual(0, result.status, result.stderr)
                self.assertEqual(expected, result.stdout)
                self.assertEqual([], case.calls())


class InterpreterTests(LandTestCase):
    def test_bash_is_the_version_this_run_requires(self):
        # Set by CI's macOS job, so a runner image whose /bin/bash is no longer 3.2 fails the job
        # instead of quietly testing a bash the script's header does not target.
        required = os.environ.get(REQUIRE_BASH, "")
        version = bash_version()
        self.assertRegex(version, r"^\d+\.\d+$")
        if required:
            self.assertEqual(required, version, f"{REQUIRE_BASH}={required}, but {BASH} is {version}")

    def test_runs_through_its_own_shebang(self):
        self.assertTrue(os.access(SCRIPT, os.X_OK), f"{SCRIPT} is not executable")
        case = Case(self)
        # `#!/usr/bin/env bash` finds bash on PATH, which here is only the case's bin directory.
        (case.bin / "bash").symlink_to(BASH)
        result = case.run(through_shebang=True)
        self.assertRefused(case, result, "which pull request?", [])


class CompletenessTests(unittest.TestCase):
    """Runs last (load_tests below), and alone once every other case has finished: every `die` in
    land.sh was produced by some case."""

    runs_after_the_others = True

    DIE = re.compile(r'\bdie "((?:[^"\\]|\\.)*)"')
    # Every use of die, whatever follows it: all but the function's own definition. Whole-line
    # comments are dropped first, so prose naming die is not counted as a use.
    DIE_USE = re.compile(r"\bdie\b(?!\s*\(\))")
    COMMENT = re.compile(r"^\s*#.*$", re.MULTILINE)
    VARIABLE = re.compile(r"\$(?:\{[^}]*\}|[A-Za-z_][A-Za-z0-9_]*|[0-9#*@?$!-])")

    def pattern(self, message: str) -> re.Pattern:
        """The `land:` line a `die "<message>"` prints, with each expansion matching anything."""
        literals = [re.sub(r'\\([\\"$`])', r"\1", part) for part in self.VARIABLE.split(message)]
        return re.compile("^land: " + ".*".join(re.escape(part) for part in literals) + "$",
                          re.MULTILINE)

    def test_every_refusal_has_a_case(self):
        # The whole script, not only the part above `gh pr merge`: the fast-forward's refusal comes
        # after the merge, and a refusal added past it would otherwise go unseen too (#97).
        code = self.COMMENT.sub("", SCRIPT.read_text())
        messages = self.DIE.findall(code)
        self.assertTrue(messages, "found no `die` in land.sh; this case would check nothing")
        # A refusal this case cannot read would otherwise pass unseen: a single-quoted message is not
        # found at all, and one with no literal word, such as die "$reason", matches any land: line.
        unread = [line.strip() for line in code.splitlines()
                  if len(self.DIE_USE.findall(line)) != len(self.DIE.findall(line))]
        unread += [f'die "{message}"' for message in messages
                   if not re.search(r"\w", "".join(self.VARIABLE.split(message)))]
        self.assertEqual([], unread, "a `die` in land.sh that this case cannot match to a land: "
                                     "line; give it a double-quoted message with a literal word")
        seen = "\n".join(OBSERVED)
        missing = [message for message in messages if not self.pattern(message).search(seen)]
        self.assertEqual([], missing, "a refusal in land.sh that no case produced")


def load_tests(loader, tests, pattern):
    """The module's tests in their usual order, with the completeness case moved to the end."""
    def flatten(suite):
        for test in suite:
            if isinstance(test, unittest.TestSuite):
                yield from flatten(test)
            else:
                yield test
    everything = list(flatten(tests))
    last = [test for test in everything if isinstance(test, CompletenessTests)]
    return unittest.TestSuite([test for test in everything if test not in last] + last)


if __name__ == "__main__":
    runner.main()
