#!/usr/bin/env python3
"""Test companion for tools/land.sh: every way it stops before the merge, and what it hands gh.

Owner: integrator. Decided in #66 (archived overnight ticket 69, Q1 to Q20); the tool choice is D-3
in docs/dependencies.md.

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

**Real git; a double for gh and for python3.** Every git call land.sh makes before its merge works
offline, so each case builds a bare `origin` and a clone of it in its throwaway directory and lets
the refusals meet git itself. gh is the one call that leaves the machine, so each case's bin holds a
strict gh double: it answers the three `pr view` shapes land.sh uses, the two `api` listings of the
pull request's comments and reviews (from the case's own fixtures), and `pr merge`; it records every
call's arguments and fails the merge, so no case gets past it. Any other call is recorded and exits
97, which turns a new gh call in land.sh into a red case instead of an empty answer. land.sh runs
python3 twice. Before its merge it runs tools/review_record.py beside it, and the double hands that
call to the real interpreter, so every case reads its records with the real reader (#67). After the
merge it derives the roadmap, so the double fails any other call. The merge body is an argument, so
its bytes are in the record of the merge call, and they are what expose a wrong path: a body path
checked in one directory and read in another reaches gh as other bytes or none.

**No detached housekeeping.** Each repository, clone and bare origin is made with `gc.auto=0` and
`maintenance.auto=false`. The cases fetch and commit, which is when git may start `gc --auto` or
`maintenance run --auto` in the background, and a background writer is a cleanup race.

**A refusal is any exit before the merge call**, with or without a `land:` line. Each `die` above
`gh pr merge` has a case, and the last case to run reads land.sh and fails naming any of them that
no case produced, or whose message it cannot match to a `land:` line, so a refusal added later
without a case turns this companion red. Run the whole file: a subset leaves that closing case with
less to see.

Each case asserts the exit status, the `land:` line by the fragment that names the refusal (not the
whole sentence, so a rewording is not a test edit), and exactly what the gh double recorded.

Run: python3 -I tools/land_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "land.sh"
READER = ROOT / "tools" / "review_record.py"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

# The real tools land.sh runs before its merge, besides gh and python3, which get doubles. Each is
# reached from the case's bin directory, which is the whole of the case's PATH.
TOOLS = ("git", "dirname", "basename", "awk")

PR = "5"
VIEW_STATE = ["pr", "view", PR, "--json", "state", "--jq", ".state"]
VIEW_BASE = ["pr", "view", PR, "--json", "baseRefName", "--jq", ".baseRefName"]
VIEW_HEAD = ["pr", "view", PR, "--json", "headRefOid", "--jq", ".headRefOid"]
LIST_COMMENTS = ["api", "repos/{owner}/{repo}/issues/" + PR + "/comments", "--paginate", "--slurp"]
LIST_REVIEWS = ["api", "repos/{owner}/{repo}/pulls/" + PR + "/reviews", "--paginate", "--slurp"]
# What land.sh asks gh before any refusal that reads the review records, in order.
READS_RECORDS = [VIEW_STATE, VIEW_BASE, VIEW_HEAD, LIST_COMMENTS, LIST_REVIEWS]

# The pull request's head as the double reports it, an earlier head a review once read, the base a
# record names, and a head the pull request moves to while land.sh runs.
HEAD = "1a954628a960aaef81d7b2d4521929579f3541e6"
EARLIER = "f12e996adf405952c9666956e73952681bc053d8"
BASE_SHA = "1405df66cbe219b0bf6355bc3d60361a8376b6b4"
MOVED = "d0941e68da8f38151ff86a61fc59f7c5cf9fcaa2"
MERGE = ["pr", "merge", PR, "--merge", "--match-head-commit", HEAD]
RECORD_URL = f"https://github.com/adammedford/spa-platform/pull/{PR}#issuecomment-501"
REVIEWED = f"Reviewed: {HEAD} {RECORD_URL}"


def record_comment(head: str, verdict: str, url: str = RECORD_URL) -> dict:
    """A pull request comment carrying a review record, in the shape GitHub's REST API lists it:
    https://docs.github.com/en/rest/issues/comments#list-issue-comments. The line is written out
    here rather than read from the reader, which is the thing under test."""
    return {"html_url": url, "body": f"## Standards\n\nPASS.\n\nReviewed {head} against {BASE_SHA}: {verdict}\n",
            "created_at": "2026-10-18T01:00:00Z", "user": {"login": "adammedford"}}


CLEARS = [record_comment(HEAD, "merge")]

# Every standard error a case saw, for the closing completeness case.
OBSERVED: list[str] = []

GH_DOUBLE = """#!/bin/bash
# A double for gh, written by tools/land_tests.py for one case. It answers only the calls
# tools/land.sh makes up to its merge, in the shapes gh's manual gives them:
#
#   gh pr view <number> --json <field> --jq <expression>     https://cli.github.com/manual/gh_pr_view
#   gh api <endpoint> --paginate --slurp                     https://cli.github.com/manual/gh_api
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
view_fails={view_fails}
api_fails={api_fails}

n=0
while [ -e "$log/call-$n" ]; do n=$((n + 1)); done
printf '%s\\0' "$@" > "$log/call-$n"

unexpected() {{
  : > "$log/unexpected"
  printf 'gh double: unexpected call, recorded as call-%s\\n' "$n" >&2
  exit 97
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
        shift 6
        if [ "$#" -ge 2 ] && [ "$1" = --subject ]; then shift 2; fi
        [ "$#" -eq 2 ] && [ "$1" = --body ] || unexpected
        printf 'gh double: the merge fails on purpose, so no case goes past it\\n' >&2
        exit 1 ;;
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
  *) unexpected ;;
esac
"""

PYTHON_DOUBLE = """#!/bin/bash
# A double for python3, written by tools/land_tests.py. Before its merge land.sh runs the review
# record reader that sits beside it, and that call goes to the real interpreter, so the reader under
# test is the real one. Its only other python3 call comes after the merge, to derive the roadmap, so
# a case that reaches the line below has gone past a merge that should have failed.
if [ "$#" -eq 3 ] && [ "$1" = -I ] && [ "$2" = {reader} ]; then exec {python} "$@"; fi
: > {log}/python3-ran
printf 'python3 double: ran, so land.sh went past its merge\\n' >&2
exit 98
"""


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
                 reviews: list | None = None, view_fails: str = "", api_fails: str = "",
                 gh: bool = True, python3: bool = True):
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
            # A one-line exec of the resolved binary rather than a symbolic link: a git built with
            # RUNTIME_PREFIX finds its helper programs relative to the path it was started from
            # (https://github.com/git/git/blob/master/Makefile), and through a link that path
            # would be this directory.
            self.write_executable(tool, f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        # One page each, as `gh api --paginate --slurp` wraps them (`gh api --help`).
        listings = {"comments": CLEARS if comments is None else comments, "reviews": reviews or []}
        for name, entries in listings.items():
            (self.dir / f"{name}.json").write_text(json.dumps([entries]))
        if gh:
            self.write_executable("gh", GH_DOUBLE.format(
                log=shlex.quote(str(self.log)), pr=shlex.quote(PR), state=shlex.quote(state),
                base=shlex.quote(base), heads=shlex.quote(" ".join(heads)),
                comments=shlex.quote(str(self.dir / "comments.json")),
                reviews=shlex.quote(str(self.dir / "reviews.json")),
                view_fails=shlex.quote(view_fails), api_fails=shlex.quote(api_fails)))
        if python3:
            self.write_executable("python3", PYTHON_DOUBLE.format(
                log=shlex.quote(str(self.log)), reader=shlex.quote(str(READER)),
                python=shlex.quote(os.path.realpath(sys.executable))))

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

    def env(self, **extra: str) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "GIT_CONFIG_NOSYSTEM": "1",
               "GIT_CONFIG_GLOBAL": str(self.gitconfig), "LC_ALL": "C"}
        env.update(extra)
        return env

    def git(self, *args: str, cwd: pathlib.Path):
        subprocess.run(["git", *args], cwd=cwd, env=self.env(), check=True, capture_output=True)

    def quiet_housekeeping(self, repository: pathlib.Path):
        self.git("config", "gc.auto", "0", cwd=repository)
        self.git("config", "maintenance.auto", "false", cwd=repository)

    def clone(self, source: str, target: pathlib.Path, *options: str):
        self.git("clone", "-q", "-c", "gc.auto=0", "-c", "maintenance.auto=false", *options,
                 source, str(target), cwd=self.dir)
        self.git("config", "user.name", "Land Tests", cwd=target)
        self.git("config", "user.email", "land-tests@example.invalid", cwd=target)

    def run(self, *args: str, cwd: pathlib.Path | None = None, stdin: bytes = b"",
            env: dict[str, str] | None = None, through_shebang: bool = False) -> Result:
        command = [str(SCRIPT), *args] if through_shebang else [BASH, str(SCRIPT), *args]
        completed = subprocess.run(command, cwd=cwd or self.work, env=env or self.env(),
                                   input=stdin, capture_output=True, timeout=60)
        result = Result(completed)
        OBSERVED.append(result.stderr)
        return result

    def calls(self) -> list[list[str]]:
        names = sorted(self.log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        return [path.read_bytes().decode("utf-8", "surrogateescape").split("\0")[:-1]
                for path in names]


class LandTestCase(unittest.TestCase):
    def assertNoStrayCall(self, case: Case):
        """No gh call the double does not answer, and no python3, which runs only past the merge."""
        self.assertFalse((case.log / "unexpected").exists(),
                         f"land.sh made a gh call the double does not answer: {case.calls()}")
        self.assertFalse((case.log / "python3-ran").exists(), "land.sh ran python3, past its merge")

    def assertRefused(self, case: Case, result: Result, fragment: str, calls: list[list[str]]):
        """A refusal: status 1, a `land:` line naming it, and gh asked exactly `calls`."""
        self.assertEqual(1, result.status, result.stderr)
        self.assertTrue(any(fragment in line for line in result.land_lines()),
                        f"no land: line containing {fragment!r} in:\n{result.stderr}")
        self.assertEqual(calls, case.calls())
        self.assertNoStrayCall(case)


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

    def test_a_record_in_a_commented_review_clears_as_a_comment_does(self):
        review = {"html_url": f"https://github.com/adammedford/spa-platform/pull/{PR}#pullrequestreview-7",
                  "body": f"Reviewed {HEAD} against {BASE_SHA}: merge", "state": "COMMENTED",
                  "submitted_at": "2026-10-18T01:00:00Z", "commit_id": HEAD}
        case = Case(self, comments=[], reviews=[review])
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("the merge fails on purpose", result.stderr)
        self.assertEqual(f"Reviewed: {HEAD} {review['html_url']}", case.calls()[-1][-1])


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

    def test_gh_api_listing_fails(self):
        case = Case(self, api_fails="gh: Server Error (HTTP 502)")
        result = case.run(PR, "--subject", "Merge task 9.9: x")
        self.assertEqual(1, result.status, result.stderr)
        self.assertIn("HTTP 502", result.stderr)
        self.assertEqual([VIEW_STATE, VIEW_BASE, VIEW_HEAD, LIST_COMMENTS], case.calls())
        self.assertNoStrayCall(case)


class MergeCallTests(LandTestCase):
    """What gh receives at the merge: the exact arguments, the head they pin and the body's bytes.
    The double fails every merge, so each case also shows that a failed merge stops everything
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
                               before=[VIEW_STATE, VIEW_BASE, VIEW_HEAD])

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
            before=[VIEW_STATE, VIEW_BASE, VIEW_HEAD])

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

    def test_body_file_named_with_a_leading_hyphen(self):
        # dirname and basename read a leading hyphen as an option unless `--` ends their options.
        case = Case(self)
        (case.work / "-x.md").write_bytes(self.BODY.encode())
        result = case.run(PR, "--subject", "Merge task 9.9: x", "--body-file", "-x.md")
        self.assertMergeCalled(
            case, result,
            MERGE + ["--subject", "Merge task 9.9: x", "--body", f"{self.BODY}\n\n{REVIEWED}"])


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
    """Runs last (load_tests below): every `die` above the merge call was produced by some case."""

    MERGE_CALL = re.compile(r"^\s*gh pr merge\b", re.MULTILINE)
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

    def test_every_refusal_above_the_merge_call_has_a_case(self):
        text = SCRIPT.read_text()
        merge = self.MERGE_CALL.search(text)
        self.assertIsNotNone(merge, "land.sh has no `gh pr merge` line to read refusals above")
        above = self.COMMENT.sub("", text[:merge.start()])
        messages = self.DIE.findall(above)
        self.assertTrue(messages, "found no `die` above the merge call; this case would check nothing")
        # A refusal this case cannot read would otherwise pass unseen: a single-quoted message is not
        # found at all, and one with no literal word, such as die "$reason", matches any land: line.
        unread = [line.strip() for line in above.splitlines()
                  if len(self.DIE_USE.findall(line)) != len(self.DIE.findall(line))]
        unread += [f'die "{message}"' for message in messages
                   if not re.search(r"\w", "".join(self.VARIABLE.split(message)))]
        self.assertEqual([], unread, "a `die` above the merge call that this case cannot match to a "
                                     "land: line; give it a double-quoted message with a literal word")
        seen = "\n".join(OBSERVED)
        missing = [message for message in messages if not self.pattern(message).search(seen)]
        self.assertEqual([], missing, "a refusal above the merge call that no case produced")


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
    unittest.main()
