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
# port the caller names, and `terraform test` for every root and module that has tests. Its lines
# say "check:", and it ends on one line, `check: pass: ...` or `check: fail: ...`, exiting non-zero
# on failure, so a log's last line is the verdict. Two things differ from the default mode, both
# because a verdict is read without the lines above it:
#   - It shows the build's and Terraform's own output, which the hook sends to /dev/null: a lane
#     reads its failure from the log, and a hidden compiler error costs it a second run.
#   - A tool a check needs that is not on PATH fails it, where the hook warns and passes. So does
#     having no base to judge against. A green verdict that skipped a check is the failure CODING_
#     STANDARDS.md describes as "a test that skips on the machine it runs on".
#
# Both modes skip the same halves: the build, the tests and the smoke test when no .NET source
# changed, and Terraform and its tests when nothing under infra/ did, nor the floor's file or its
# helper. .github/touches.sh says what each half is, and CI's jobs skip by the same classifier, so a
# green full check predicts CI's verdict on the same diff. CI additionally runs both halves when its
# workflow, the classifier or tools/smoke.sh changes; this check does not, so a change to one of
# those is proved in CI.
#
# Both modes run Terraform at the floor, the version CI runs, when this machine has it, and name the
# version in the last line either way (#69). Without the floor they run the terraform on PATH and
# say so in a warning and in that line, rather than fail: the check still catches most of what it
# did, and the substitution is visible to whoever reads the verdict. tools/terraform-floor.sh says
# where the floor lives, and tools/preflight.sh says how to install it.
#
# --both-halves runs both halves whatever the diff touches. On main the diff against origin/main is
# empty, so without it the full check would pass having run none of the three things it adds; #68's
# second box asks for the full mode green on main, and only this makes that run test anything. The
# hook never passes it, and a lane does not need it: its diff earns its skips.
#
# --base names what the branch is judged against, for A-05 and for the halves. The default is the
# default branch, origin/main, else main: the question is what the branch adds to main. An overnight
# lane names its fork point instead, so its own commits are judged rather than the night's merges.
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
# the classifier or tools/smoke.sh. With no base to diff against, assume this change touches both
# halves: a skipped check has to be earned by a diff that proves it unnecessary. --both-halves asks
# for both outright; the header says when.
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
if command -v python3 > /dev/null; then
  python3 -I .github/steering.py || fail "the steering check failed."
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

# --- the build, and in full the tests and the smoke test ----------------------------------------
#
# Warnings are errors, so this catches what a reviewer should never have to. Skipped when nothing
# that compiles changed, and .github/touches.sh says what compiles.
#
# The full check then runs what CI's Build and test job runs after its build, on this build: every
# test project, where 8 is Microsoft Testing Platform's "zero tests" and expected of a project that
# has none yet, and G1a's exit test, which nothing else proves. CI builds Release; this builds the
# default Debug, as the hook always has, and smokes what it built.
ran="A-05, the steering documents, every ticket citation, the ticket numbers, the roadmap, the artifacts"

if touches build; then
  command -v dotnet > /dev/null || fail "dotnet is not on PATH."
  quietly dotnet build --nologo --verbosity quiet || fail "the solution does not build."
  ran="$ran, the build"
  if $full; then
    dotnet test --solution SpaPlatform.slnx --no-build --ignore-exit-code 8 || fail "the tests failed."
    tools/smoke.sh Debug "$port" || fail "the smoke test failed."
    ran="$ran, the tests, the smoke test"
  fi
elif $full; then
  echo "check: build, tests and smoke test skipped: no .NET source changed."
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

verdict="pass"
if $full; then
  echo "check: pass: $ran."
else
  echo "pre-push: $ran pass."
fi
