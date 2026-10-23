#!/usr/bin/env bash
# The local check: what a branch must pass on this machine. Owner: integrator.
#
#     tools/check.sh [--base <ref>]                          what .githooks/pre-push runs
#     tools/check.sh --full --port <port> [--base <ref>]     and what CI runs that the hook leaves to it
#     ... [--both-halves]                                    either, without the skips
#
# Everything that checks a branch runs this file as written: the pre-push hook, a person before a
# push, and every lane of the overnight run (#68). There used to be two copies. The hook held its
# checks, and the overnight workflow held its own build-and-test command as a string inside a
# JavaScript template literal, pasted into every lane's prompt. On the 2026-10-17 run that string
# wrote `\;`, which a template literal renders as a bare `;`, zsh refused to parse the whole
# command, and 15 agents spent about 85 tool calls repairing it before any could run. A file has no
# quoting layer to lose a character in, and a change to it reaches every caller at once.
#
# The default mode is the hook's checks, as they were when they lived in the hook: the same checks,
# the same skips and the same messages, which is why its lines say "pre-push:". The exception is the
# Terraform half, which #69 changed on purpose: its skip message names the floor too, a line names
# the Terraform it ran, and the last line names that version. They cost about a second, plus the
# build and Terraform when the change touches their half.
#
# --full adds what CI runs and the hook leaves to it: the .NET test suite, tools/smoke.sh on the
# port the caller names, tools/image-check.sh, `terraform test` for every root and module that has
# tests, and the test companions the change earns: that of each script in tools/ or .github/ it
# touches (#134), and that of each script or companion naming a library it touches, followed through
# the libraries that name that one (#237). CI runs the companions .github/workflows/ci.yml lists,
# one step each, whatever the diff, so that file says which run there; this check runs only those
# the diff earns, CI's or not. Neither side's list is copied into the other, so neither goes stale
# when the other changes. Last, when a file under .claude/workflows/ changed, it runs every workflow
# script's test companion with node, and fails on a script that has none (#276); those CI runs on
# the same answer from .github/touches.sh, in one step. Neither mode runs tools/check_tests.py,
# which runs this file and so stays CI's. Its lines say "check:", and it ends on one line, `check: pass: ...` or `check: fail: ...`,
# exiting non-zero on failure, so a log's last line is the verdict. Its first two lines name the
# bash running it and the file-mode mask, so a log can be compared with another machine's (#230).
#
# tools/image-check.sh builds the gateway's image the way a workstation does, from a copy of the
# tree it has first given a host obj/ and an owner-only appsettings.json, and starts it as the
# image's runtime user (#210). It catches the two defects the first deployment found and a clean
# checkout cannot show: host build output reaching the image's restore, which .dockerignore keeps
# out, and a file the runtime user cannot read, which the Dockerfile's chmod clears and which kills
# the gateway in WebApplication.CreateBuilder (#58, #204). It needs Docker, so a full check without
# it fails. It runs with the build half, and a change to .dockerignore is in that half.
#
# Two things differ from the default mode, both because a verdict is read without the lines above
# it:
#   - It shows the build's and Terraform's own output, which the hook sends to /dev/null: a lane
#     reads its failure from the log, and a hidden compiler error costs it a second run.
#   - A tool a check needs that is not on PATH fails it, where the hook warns and passes. So does
#     having no base to judge against. A green verdict that skipped a check is the failure CODING_
#     STANDARDS.md describes as "a test that skips on the machine it runs on".
#
# Both modes skip the same halves: the build, the tests, the smoke test and the gateway image when
# no .NET source changed, and Terraform and its tests when nothing under infra/ did, nor the floor's
# file or its helper. .github/touches.sh says what each half is, and CI's jobs skip by the same
# classifier, so a green full check predicts CI's verdict on the same diff. CI additionally runs
# both halves when its workflow, the classifier, tools/smoke.sh or tools/image-check.sh changes;
# this check does not, so a change to one of those is proved in CI, and tools/image-check.sh by its
# companion here as well.
#
# Both modes run Terraform at the floor, the version CI runs, when this machine has it, and name the
# version in the last line either way (#69). Without the floor they run the terraform on PATH and
# say so in a warning and in that line, rather than fail: the check still catches most of what it
# did, and the substitution is visible to whoever reads the verdict. tools/terraform-floor.sh says
# where the floor lives, and tools/preflight.sh says how to install it.
#
# --both-halves runs both halves whatever the diff touches, and every test companion the full check
# would run for some diff. On main the diff against origin/main is empty, so without it the full
# check would pass having run none of the things it adds; #68's second box asks for the full mode
# green on main, and only this makes that run test anything. The hook never passes it, and a lane
# does not need it: its diff earns its skips.
#
# --base names what the branch is judged against, for A-05, the steering check's authority rule and
# the halves. The default is the default branch, origin/main, else main: the question is what the
# branch adds to main. An overnight lane names its fork point instead, so its own commits are
# judged rather than the night's merges.
#
# Not called a gate: GLOSSARY.md reserves Gate for the platform's checkpoint ladder.
#
# Written for the bash 3.2 macOS ships, whatever shell calls it: no associative arrays, no
# mapfile, no ${var,,}, and no arrays at all, since under set -u bash 3.2 calls an empty
# "${array[@]}" unbound. tools/check_tests.py runs it under /bin/bash, and CI runs that companion
# under bash 3.2 on macOS as well as bash 5 on Linux.

set -euo pipefail

usage() {
  echo "usage: tools/check.sh [--base <ref>] [--both-halves]" >&2
  echo "       tools/check.sh --full --port <port> [--base <ref>] [--both-halves]" >&2
  echo "check: fail: $1." >&2
  exit 2
}

full=false
both=false
port=""
named_base=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --full) full=true ;;
    --both-halves) both=true ;;
    --port)
      [ "$#" -ge 2 ] || usage "--port needs a value"
      port="$2"
      shift
      ;;
    --base)
      [ "$#" -ge 2 ] || usage "--base needs a value"
      named_base="$2"
      shift
      ;;
    *) usage "unknown argument $1" ;;
  esac
  shift
done

if $full; then
  [ -n "$port" ] || usage "--full needs --port <port>"
  case "$port" in
    '' | *[!0-9]*) usage "--port takes a number, not $port" ;;
  esac
  me="check"
else
  [ -z "$port" ] || usage "--port is for --full"
  me="pre-push"
fi

# The verdict, in full mode, for every way out: fail() below writes its own and exits, and anything
# else that stops the script under set -e reaches this line instead of ending the log mid-step.
verdict=""
on_exit() {
  status=$?
  if $full && [ "$status" -ne 0 ] && [ -z "$verdict" ]; then
    echo "check: fail: stopped with status $status." >&2
    exit 1
  fi
}
trap on_exit EXIT

fail() {
  verdict="fail"
  if $full; then
    echo "check: fail: $1" >&2
  else
    echo "pre-push: $1" >&2
    echo "pre-push: fix it, or push with --no-verify if you mean it." >&2
  fi
  exit 1
}

# A tool a check needs. The hook warns and skips the check ("<what> not checked: ..."); the full
# check fails, for the reason the header gives.
missing() {
  if $full; then
    fail "$1 is not on PATH."
  fi
  echo "pre-push: $2 not checked: $1 is not on PATH." >&2
}

# The full check shows what the hook sends to /dev/null; the header says why.
quietly() {
  if $full; then
    "$@"
  else
    "$@" > /dev/null
  fi
}

# --- in full, the environment, before any check -----------------------------------------------
#
# Two lanes, #205's and #210's, found a companion red on their fork point that passed on main and
# in CI, and #205's had had to force file modes under a restrictive mask; neither log said which
# bash or which mask it ran under, so nothing could be compared (#230). The bash is this script's
# interpreter, which is the one whose version matters (the bash 3.2 note above), not the caller's
# login shell. The mask is printed as `umask` prints it, never normalised: 0077 is the finding.
# Before the first check, so a log that stops at one still has both.
if $full; then
  echo "check: shell: $BASH, bash $BASH_VERSION"
  echo "check: file-mode mask: $(umask)"
fi

root="$(git rev-parse --show-toplevel)"
cd "$root"

# --- the author email -------------------------------------------------------------------------
#
# GitHub refuses a push carrying a private email when the account asks it to, and by then the
# history already has it: the only ways out are rewriting every commit or weakening an
# account-wide setting. Catch it at the first commit instead of the thirteenth.
noreply_domain="users.noreply.github.com"
author_email="$(git config user.email || true)"

[ -n "$author_email" ] || fail "git config user.email is unset."

case "$author_email" in
*@"$noreply_domain")
  : # The address GitHub hands out for exactly this. Nothing to check.
  ;;
*)
  echo "$me: warning: committing as '$author_email', not an @${noreply_domain} address." >&2
  echo "$me: if that address is private on GitHub, it will refuse the push." >&2
  ;;
esac

# --- check A-05: path ownership ---------------------------------------------------------------
#
# The only place this runs. A failure here is one amended commit; the same failure found after a
# push would cost a red pull request and buy nothing, since the same person reviews it either way.
# There is no override but --no-verify, which is the point: an override nobody can verify is a
# permissions model for a team of one.
#
# An integration branch is judged per commit instead of by its whole diff, so a branch that merges
# single-owner lanes passes here rather than needing --no-verify -- which would have skipped the
# other checks below it as well. .github/path-ownership.py carries the reasoning.
#
# The default branch, not this branch's upstream. The question is what this branch adds to main;
# against its own upstream it would compare a branch to itself after the first push and pass on an
# empty diff every time. A base the caller names must resolve: a typo there would otherwise judge
# nothing and pass.
if [ -n "$named_base" ]; then
  base="$named_base"
  git rev-parse --verify --quiet "$base^{commit}" > /dev/null || fail "no commit $base to judge against."
else
  base="origin/main"
  git rev-parse --verify --quiet "$base^{commit}" > /dev/null || base="main"
fi

if git rev-parse --verify --quiet "$base^{commit}" > /dev/null; then
  changed="$(git diff --name-only "$base"...HEAD)"
else
  base=""
fi

# Whether this change touches a half. What counts as a half is .github/touches.sh's to say, and CI
# reads the same patterns. CI's decision step additionally runs both jobs for changes to the workflow,
# the classifier, tools/smoke.sh or tools/image-check.sh. With no base to diff against, assume this
# change touches both halves: a skipped check has to be earned by a diff that proves it
# unnecessary. --both-halves asks for both outright; the header says when.
touches() {
  $both || [ -z "$base" ] || printf '%s\n' "$changed" | .github/touches.sh "$1"
}

if command -v python3 > /dev/null; then
  if [ -n "$base" ]; then
    python3 -I .github/path-ownership.py "$base" HEAD || fail "check A-05 failed."
  elif $full; then
    fail "no origin/main or main to judge against; name one with --base."
  else
    echo "pre-push: A-05 not checked: no main to diff against." >&2
  fi
else
  missing python3 A-05
fi

# --- the documents a session steers by, every ticket any file cites, and the tickets' numbers ---
#
# AGENTS.md and CLAUDE.md are loaded into every session, and the contracts index is read by every
# workstream before it builds. A progress claim, a pointer at a file that does not exist, a freeze
# cell the contract it indexes denies, or a ticket number that resolves to no spec costs every
# later session, so catch it at the push that writes it.
# The citation rule is not scoped to those documents: an ADR, a contract or a code comment cites
# tickets too, so it reads every tracked file. One consequence worth knowing before it surprises
# someone: deleting a ticket file turns every citation of it red, in whatever push comes next and
# whatever it happened to touch.
# The numbering rule fails when two tickets in one tracker directory share a number, which is how
# two sessions that filed concurrently find out at the second push rather than after it. This is
# where that has to be caught: neither session's branch is wrong until the other's lands.
# The issue rule reads every `#NNN` the same files cite and asks GitHub, through gh, whether the
# number is an issue or pull request here. Without gh, a login or a network it says so and fails
# nothing, so an offline push is not held up on it; CI asks again with its own token.
# The authority rule reads commits rather than files: every commit from the base A-05 judges
# against to HEAD that changes a steering document needs an `Authority:` trailer naming what
# decided the change (ADR-0023, #102), so it is handed that base. With none, it reads every commit
# HEAD reaches, so no base is not a skip. Offline it still requires the trailer, and says it could
# not check what the trailer cites.
if command -v python3 > /dev/null; then
  if [ -n "$base" ]; then
    python3 -I .github/steering.py --base "$base" || fail "the steering check failed."
  else
    python3 -I .github/steering.py || fail "the steering check failed."
  fi
else
  missing python3 steering
fi

# --- the roadmap ------------------------------------------------------------------------------
#
# docs/roadmap.md states how far the work has got, which is the one thing a document must never
# state from memory. Its block is derived from the tags, the merge subjects and the plan's task
# tables, so the push that lands a row is the push that updates it: one command, and nobody has to
# remember a number. The check also fails if the ladder stops covering every plan row exactly once,
# which is what a fresh plan export would do.
if command -v python3 > /dev/null; then
  python3 -I tools/roadmap.py --check \
    || fail "the roadmap is out of date. Run tools/roadmap.py and commit what it writes."
else
  missing python3 "the roadmap"
fi

# --- duplicated build artifacts ---------------------------------------------------------------
#
# iCloud Drive leaves files like "Platform.Abstractions 2.dll" and "project.assets 2.json" in bin/
# and obj/. A clone under ~/Documents or ~/Desktop is synced when "Desktop & Documents Folders" is
# on, and the file provider keeps a numbered copy when a build replaces a file it has not finished
# syncing. A clean checkout there held nearly two thousand of them. Keep clones and worktrees
# outside iCloud; this check is for the clone that is not.
#
# They do not fail the build. They make it lie: a project compiles clean while its consumer reports
# CS0103 on a type that is plainly there, and MSBuild reports an import "cannot be imported again"
# from a path with a space and a digit in it. That cost two debugging sessions and produced a
# confident wrong diagnosis in between, which is worse than a failure.
#
# Checked before the build, because the build is what the artifacts corrupt.
duplicates="$(find src tests fixtures tools \( -path "*/bin/*" -o -path "*/obj/*" \) -name "* [0-9].*" 2> /dev/null || true)"

if [ -n "$duplicates" ]; then
  echo "$me: duplicated build artifacts:" >&2
  echo "$duplicates" | head -5 >&2
  echo "$me: delete every bin/ and obj/ and build again, and move the clone out of iCloud Drive:" >&2
  echo "  find src tests fixtures tools -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {} +" >&2
  fail "duplicated build artifacts make the build report errors that are not in the source."
fi

# --- tracked files their owner alone can read ------------------------------------------------
#
# Git records one bit of a file's mode, the exec bit, so a tracked file written under `umask 077`
# is mode 600 on this machine while nothing in the repository shows it: no diff, no status line.
# An image built from this tree keeps the mode, unless its Dockerfile resets it, and a container
# that runs as anyone but the owner cannot read the file. On 2026-10-21 97 such files under src/,
# contracts/ and fixtures/ made the gateway's container die at WebApplication.CreateBuilder with
# UnauthorizedAccessException on /app/appsettings.json (#58, #205). CI checks out with 644 and
# never sees it, so the hook and the overnight lanes are the only places this can be caught.
#
# Read by group and others both, because the fix is `chmod go+r` and a container user is usually
# neither the owner nor in its group: a 640 file breaks the image as surely as a 600 one. Only
# regular files are judged: a symlink's own mode is not what a container reads (its tracked target
# is judged in its own right), macOS gives a link the umask's mode, and `chmod go+r` follows the
# link, so judging one would refuse a push that the named fix cannot clear (#205's review). A
# tracked path missing from the working tree is not a file to judge, so find's complaint and status
# about it are dropped inside the bash, and only there: a git or xargs that fails still fails the
# check under pipefail rather than passing it with nothing judged. xargs appends what it reads after
# the command it runs, and find wants its paths before its expression, so a bash between them puts
# them first, each behind a `./` that keeps a path beginning with `-` a path to find. Never skipped,
# because no diff shows a mode.
unreadable="$(git ls-files -z \
  | xargs -0 -r "$BASH" -c 'for p; do shift; set -- "$@" "./$p"; done
      find "$@" -prune -type f ! -perm -044 -print 2> /dev/null; :' file-modes)" \
  || fail "the file modes were not judged: git ls-files or xargs failed."

if [ -n "$unreadable" ]; then
  echo "$me: tracked files that group and others cannot read:" >&2
  shown=0
  total=0
  while IFS= read -r path; do
    total=$((total + 1))
    if [ "$shown" -lt 10 ]; then
      echo "${path#./}" >&2
      shown=$((shown + 1))
    fi
  done << EOF
$unreadable
EOF
  [ "$total" -le "$shown" ] || echo "... and $((total - shown)) more" >&2
  echo "$me: git does not record these modes, but an image built from this tree keeps them. Make them readable:" >&2
  echo "  git ls-files -z | xargs -0 chmod go+r" >&2
  fail "a tracked file only its owner can read is a file the gateway's container cannot read; run chmod go+r on it."
fi

# --- the build, and in full the tests and the smoke test ----------------------------------------
#
# Warnings are errors, so this catches what a reviewer should never have to. Skipped when nothing
# that compiles changed, and .github/touches.sh says what compiles.
#
# The full check then runs what CI's Build and test job runs after its build, on this build: every
# test project, where 8 is Microsoft Testing Platform's "zero tests" and expected of a project that
# has none yet, and G1a's exit test, which nothing else proves. CI builds Release; this builds the
# default Debug, as the hook always has, and smokes what it built. Last, the gateway's image, built
# and started as the header says; it publishes in the image, so it does not use this build.
ran="A-05, the steering documents, every ticket citation, the ticket numbers, the roadmap, the artifacts, the file modes"

if touches build; then
  command -v dotnet > /dev/null || fail "dotnet is not on PATH."
  quietly dotnet build --nologo --verbosity quiet || fail "the solution does not build."
  ran="$ran, the build"
  if $full; then
    dotnet test --solution SpaPlatform.slnx --no-build --ignore-exit-code 8 || fail "the tests failed."
    tools/smoke.sh Debug "$port" || fail "the smoke test failed."
    tools/image-check.sh || fail "the gateway image failed to build or to start as its runtime user."
    ran="$ran, the tests, the smoke test, the gateway image"
  fi
elif $full; then
  echo "check: build, tests, smoke test and gateway image skipped: no .NET source changed."
else
  echo "pre-push: build skipped: no .NET source changed."
fi

# --- Terraform, and in full its tests ----------------------------------------------------------
#
# The same argument as the build, for the other half of the repository. CI checks both; finding a
# misformatted root here costs an amended commit instead of a red pull request.
#
# -backend=false: validation needs no state and no credentials, which is also why it can run on a
# workstation that has never signed in to AWS.
#
# The full check then runs `terraform test` where CI's Terraform job does: in every directory whose
# configuration has a *.tftest.hcl beside it or in its tests/ directory, which are the two places
# Terraform loads them from (https://developer.hashicorp.com/terraform/language/tests#test-directory).
# The tests use mock providers, so they need no credentials either. Each such directory holds .tf
# files, so the loop above has already initialised it.
#
# Which Terraform: the floor, the version CI runs, when this machine has it, and otherwise the
# terraform on PATH, named as a substitute in a warning and in the last line, so a green result
# never hides it (#69). tools/terraform-floor.sh says where the floor lives and why.
terraform_half=false
terraform_bin=""
if touches terraform; then
  terraform_half=true
  . tools/terraform-floor.sh
  floor="$(terraform_floor_read .terraform-version)" \
    || fail ".terraform-version does not name one exact version, such as 1.11.0, so nothing says which Terraform CI runs."
  terraform_choose "$floor"
fi

if ! $terraform_half; then
  if $full; then
    echo "check: Terraform and its tests skipped: nothing under infra/ or the floor changed."
  else
    echo "pre-push: Terraform skipped: nothing under infra/ or the floor changed."
  fi
elif [ -n "$terraform_bin" ]; then
  if [ "$terraform_used" = "$floor" ]; then
    terraform_named="Terraform $floor"
    echo "$me: Terraform $floor from $terraform_bin: the floor, which CI runs."
  else
    terraform_named="Terraform ${terraform_used:-of no known version} instead of the floor $floor"
    echo "$me: warning: Terraform ${terraform_used:-of no known version} from $terraform_bin, not the floor $floor that .terraform-version names and CI runs." >&2
    echo "$me: tools/preflight.sh says how to install the floor." >&2
  fi

  quietly "$terraform_bin" fmt -check -recursive infra || fail "terraform fmt -check -recursive infra failed."

  for dir in $(find infra -name '*.tf' -not -path '*/.terraform/*' -exec dirname {} \; | sort -u); do
    # init's progress lines stay hidden in both modes. Its errors go to standard error, which the
    # full check shows and the hook, as it always has, does not.
    if $full; then
      "$terraform_bin" -chdir="$dir" init -backend=false -input=false > /dev/null \
        || fail "terraform init failed in $dir."
    else
      "$terraform_bin" -chdir="$dir" init -backend=false -input=false > /dev/null 2>&1 \
        || fail "terraform init failed in $dir."
    fi
    quietly "$terraform_bin" -chdir="$dir" validate || fail "terraform validate failed in $dir."
  done
  ran="$ran, $terraform_named"

  if $full; then
    tested=""
    for found in $(find infra -type f -name '*.tftest.hcl' -not -path '*/.terraform/*' -exec dirname {} \; | sort -u); do
      if [ "$(basename "$found")" = tests ]; then
        found="$(dirname "$found")"
      fi
      case " $tested " in
        *" $found "*) continue ;;
      esac
      tested="$tested $found"
    done
    [ -n "$tested" ] || fail "no Terraform test roots found under infra/."
    for dir in $(printf '%s\n' $tested | sort -u); do
      "$terraform_bin" -chdir="$dir" test || fail "terraform test failed in $dir."
    done
    ran="$ran, the Terraform tests"
  fi
else
  missing terraform Terraform
fi

# --- in full, the test companions of the scripts the change touches ------------------------------
#
# A change to tools/ or .github/ touches neither half, so without this a green full check on it
# would have run none of its code: on 2026-10-19, 7 of the 12 merged tickets were such changes, and
# their reviewers ran the companions by hand (#134). CI runs the companions
# .github/workflows/ci.yml lists, one step each, whatever the diff, and that file is where to read
# which; this runs those the diff earns, as the halves are gated, including any CI does not list.
#
# A script's companion is the file beside it named `<name>_tests.py`, its name with the extension
# dropped and hyphens as underscores (tools/land.sh's is tools/land_tests.py, and
# .github/path-ownership.py's .github/path_ownership_tests.py), or else its named test file
# `<name>-tests.py` (.github/plan-secrecy.py's is .github/plan-secrecy-tests.py). A companion runs
# when the diff touches its script, the companion itself, or a library either names (below), and
# every tracked one runs under --both-halves, which promises no skips. Each is a tracked
# `*_tests.py` or `*-tests.py` under tools/ or .github/, at any depth, tools/scripts/ included,
# whether CI lists it or not. The hook leaves them to the full check and to CI, as it does the test
# suite.
companion_names() {
  # The companions a changed path could name: itself if it is one, else the two names above.
  while IFS= read -r path; do
    dir="${path%/*}"
    name="${path##*/}"
    case "$name" in
      *_tests.py | *-tests.py)
        echo "$path"
        continue
        ;;
    esac
    stem="${name%.*}"
    echo "$dir/${stem//-/_}_tests.py"
    echo "$dir/$stem-tests.py"
  done
}

# A library is a file a script or a companion names that has no companion of its own: on 2026-10-20
# #186's review broke redact() in tools/scripts/cell-common.sh, which seed.sh, deploy.sh, rollback.sh
# and teardown.sh source, and the full check passed green, since the change touched no script with a
# companion, while deploy_tests.py alone failed 18 cases (#186 P6). So the paths a change reaches are
# what the companion names are taken from: each changed path, each tracked file under tools/ or
# .github/ that names one under tools/ or .github/, and, followed through libraries, each that names
# one of those in turn. tools/scripts/cell_documents.py is two names from deploy.sh, through
# cell-common.sh, and the companions load tools/scripts/cell_doubles.py, which names it too (#237).
#
# Three things bound it, measured over the 30 tracked companions on 2026-10-22. Without them a
# change to almost any script reached 24 of them, through the names in tools/check.sh's and the
# workflow's comments, which is a slower directory rule:
#   - A file with a companion of its own ends the chain: it earns its companion, which is what tests
#     it, and its own users earn nothing from it.
#   - A companion ends it too, whether it changed or names the change: the workflow names every
#     companion it runs, and a companion's change is its own companion's run alone.
#   - A name counts whole: tools/check.sh is not in tools/image-check.sh, nor tunnel.sh in
#     verify-tunnel.sh, nor lib.sh in lib.sh.bak. A full stop that ends a sentence ends a name too,
#     so "for logs.sh." in tools/scripts/cell_doubles.py names logs.sh (#237's review).
# A name in a comment counts as a use. That over-runs, which costs time and is safe; skipping
# comments would need a reader for each language, and a rule that fails open when it misreads one.
is_companion() {
  case "$1" in
    *_tests.py | *-tests.py) return 0 ;;
  esac
  return 1
}

# Whether $1 is a library: neither a companion nor a file with a tracked companion of its own.
is_library() {
  local candidate
  is_companion "$1" && return 1
  for candidate in $(printf '%s\n' "$1" | companion_names); do
    printf '%s\n' "$tracked" | grep -Fx -- "$candidate" > /dev/null && return 1
  done
  return 0
}

# The tracked files under tools/ or .github/ that name the path $1, by its file name, whole. git
# grep searches tracked files only, as the companions are found, and exits 1 on no match; any other
# status is a search that did not happen, and returns 2 so the caller fails rather than run less.
users_of() {
  local name status
  name="${1##*/}"
  status=0
  git grep -I -l -E -e "(^|[^A-Za-z0-9_.-])${name//./\\.}([^A-Za-z0-9_.-]|\.([^A-Za-z0-9_-]|\$)|\$)" -- tools .github || status=$?
  [ "$status" -le 1 ] || return 2
}

# Sets reach to the paths the changed paths reach, the changed paths included. It sets a variable
# rather than printing, so that fail() ends the check and not a command substitution around it.
reach_from_changes() {
  local frontier next path user users
  reach="${changed:-}"
  frontier=""
  for path in $reach; do
    case "$path" in
      tools/* | .github/*) is_companion "$path" || frontier="$frontier $path" ;;
    esac
  done
  while [ -n "$frontier" ]; do
    next=""
    for path in $frontier; do
      users="$(users_of "$path")" || fail "could not search tools/ and .github/ for the files that name $path."
      for user in $users; do
        printf '%s\n' "$reach" | grep -Fx -- "$user" > /dev/null && continue
        reach="$reach
$user"
        if is_library "$user"; then
          next="$next $user"
        fi
      done
    done
    frontier="$next"
  done
}

# Whether the companion $1 runs: the diff names it, or --both-halves asks for every one. grep -Fx
# rather than -q: -q would close the pipe at a match and fail printf under pipefail.
earned() {
  $both || printf '%s\n' "$named" | grep -Fx -- "$1" > /dev/null
}

# What the verdict adds when it ran no check of the check itself.
unrun=""
if $full; then
  tracked="$(git ls-files -- tools .github)"
  reach_from_changes
  named="$(printf '%s\n' "$reach" | companion_names)"
  companions=""
  others=""
  for found in $(git ls-files -- tools .github); do
    case "$found" in
      *_tests.py | *-tests.py) ;;
      *) continue ;;
    esac
    case "$found" in
      # tools/check_tests.py runs this script, so running it from here would recurse: every case
      # there that runs --full on a change to the check would start the companion again. It stays
      # CI's, in the Documents job and the bash 3.2 job.
      tools/check_tests.py)
        if earned "$found"; then
          echo "check: tools/check_tests.py not run: it runs this check, so CI runs it."
          others=" other"
          unrun=", but not tools/check_tests.py, which is CI's"
        fi
        continue
        ;;
    esac
    if earned "$found"; then
      companions="$companions $found"
    fi
  done

  if [ -z "$companions" ] && $both; then
    echo "check: no test companion is tracked in tools/ or .github/."
  elif [ -z "$companions" ]; then
    echo "check: test companions skipped: no${others} script with one in tools/ or .github/, nor a file one names, changed."
  else
    listed=""
    count=0
    for found in $companions; do
      echo "check: the test companion $found:"
      python3 -I "$found" || fail "the test companion $found failed."
      listed="${listed:+$listed, }$found"
      count=$((count + 1))
    done
    if [ "$count" -eq 1 ]; then
      ran="$ran, the test companion $listed"
    else
      ran="$ran, the test companions $listed"
    fi
  fi
fi

# --- in full, the workflow scripts' test companions -------------------------------------------
#
# Every workflow script under .claude/workflows/ has a test companion beside it, `<name>_tests.mjs`
# with the script's name kept whole (overnight.js's is overnight_tests.mjs, pins-sweep.js's is
# pins-sweep_tests.mjs), and on #271 all of them were run by hand before each fix round because
# nothing else ran them (#276). A change anywhere under .claude/workflows/ runs every one, since
# they share the fixtures there, and a change elsewhere skips them, as the halves skip;
# .github/touches.sh says what a workflow change is, and CI's Documents job reads the same answer
# and runs the same companions. A workflow script without its companion fails the check, naming
# the script, before any companion runs, so a script cannot lose its companion and stay green. The
# companions' own output is shown, so a red one's last lines sit above the verdict, and the check
# stops at the first. Only a tracked file directly in the folder is a script or a companion: what
# sits under fixtures/ is data. Without node the check fails, as the header says of any tool.
if $full; then
  if touches workflows; then
    workflow_scripts=""
    workflow_companions=""
    # A failed listing is not an empty folder: in a `for` list its exit status is lost, and an
    # empty list passes with nothing judged (CODING_STANDARDS.md, "A check fails closed").
    workflow_tracked="$(git ls-files -- .claude/workflows)" \
      || fail "the workflow test companions were not judged: git ls-files failed."
    for found in $workflow_tracked; do
      case "${found#.claude/workflows/}" in
        */*) ;;
        *_tests.mjs) workflow_companions="$workflow_companions $found" ;;
        *.js) workflow_scripts="$workflow_scripts $found" ;;
      esac
    done
    for found in $workflow_scripts; do
      case "$workflow_companions " in
        *" ${found%.js}_tests.mjs "*) ;;
        *) fail "the workflow script $found has no test companion; add ${found%.js}_tests.mjs beside it." ;;
      esac
    done
    if [ -z "$workflow_companions" ]; then
      echo "check: no workflow test companion is tracked in .claude/workflows/."
    else
      command -v node > /dev/null || missing node "the workflow test companions"
      listed=""
      count=0
      for found in $workflow_companions; do
        echo "check: the workflow test companion $found:"
        node "$found" || fail "the workflow test companion $found failed."
        listed="${listed:+$listed, }$found"
        count=$((count + 1))
      done
      if [ "$count" -eq 1 ]; then
        ran="$ran, the workflow test companion $listed"
      else
        ran="$ran, the workflow test companions $listed"
      fi
    fi
  else
    echo "check: workflow test companions skipped: nothing under .claude/workflows/ changed."
  fi
fi

verdict="pass"
if $full; then
  echo "check: pass: $ran$unrun."
else
  echo "pre-push: $ran pass."
fi
