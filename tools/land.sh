#!/usr/bin/env bash
# Lands a reviewed pull request: merge, regenerate the roadmap, push. Owner: integrator.
#
#     tools/land.sh <pr-number> [--subject "<merge subject>"] [--body-file <path>]
#
# Run it from a worktree sitting at the pull request's base with nothing uncommitted, because the
# roadmap is derived from the tree the merge commit makes:
#
#     git worktree add --detach ../spa-land origin/main
#
# --subject is the merge subject a check reads. --body-file is a file holding the merge body, passed
# through to `gh pr merge --body-file`. The body says what was verified and how, and it carries the
# `Outstanding:` line and the `Outstanding test ids:` line when a row landed without being finished.
# Both merge subject forms and what the body owes are in docs/agents/landing-work.md -> "Land it".
#
# Why this exists rather than `gh pr merge`. tools/roadmap.py derives how far the work has got from
# the merge subjects, so the merge commit is the input to the derivation and docs/roadmap.md is its
# output. .githooks/pre-push holds the two together, and a merge made through the GitHub API never
# reaches that hook: the commit is created on the server. Four merges landed that way on 15 October
# 2026, and main went red on the roadmap check until a later commit refreshed the blocks.
#
# So: merge with --merge-queue off and the subject the pull request body asks for, fetch the merge
# commit, run tools/roadmap.py on it, and push the refresh as its own commit when the blocks moved.
# The refresh is a separate commit on purpose -- it is generated, and a reviewer should be able to
# see that nothing else rode in with it.
#
# Written for the bash 3.2 that ships with macOS.

set -euo pipefail

die() { printf 'land: %s\n' "$*" >&2; exit 1; }

pr=""
subject=""
body_file=""
while [ $# -gt 0 ]; do
  case "$1" in
    --subject) [ $# -ge 2 ] || die "--subject needs a value."; subject="$2"; shift 2 ;;
    # Checked here rather than at the merge: a path that turns out to be unreadable after the merge
    # leaves the merge done and the roadmap stale, which is what this script exists to prevent.
    --body-file)
      [ $# -ge 2 ] || die "--body-file needs a value."
      [ -f "$2" ] || die "--body-file: $2 is not a file."
      [ -r "$2" ] || die "--body-file: $2 is not readable."
      # Made absolute here, because the script changes to the repository root before the merge and
      # a relative path checked from a subdirectory would then name a different file, or none.
      # CDPATH is cleared because an exported one sends `cd` to another directory of the same name,
      # and `cd` prints it, so the path would name another file over two lines. `--` ends each
      # command's options, so a name starting with a hyphen is a name. tools/land_tests.py has both.
      body_file="$(CDPATH= cd -- "$(dirname -- "$2")" && pwd)/$(basename -- "$2")"; shift 2 ;;
    # Reads the header rather than naming the lines it sits on: a hard-coded range has to be edited
    # again every time the header moves, and nothing here would catch it when it was not.
    -h|--help) awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"; exit 0 ;;
    -*) die "unknown option $1." ;;
    *) [ -z "$pr" ] || die "one pull request at a time."; pr="$1"; shift ;;
  esac
done

[ -n "$pr" ] || die "which pull request? tools/land.sh <pr-number>"
command -v gh >/dev/null 2>&1 || die "gh is not on PATH. tools/preflight.sh says what is missing."
command -v python3 >/dev/null 2>&1 || die "python3 is not on PATH, so the roadmap cannot be derived."

root="$(git rev-parse --show-toplevel)"
cd "$root"

# The roadmap is derived from every tag and every merge subject, so a shallow clone cannot land.
[ "$(git rev-parse --is-shallow-repository)" = "false" ] \
  || die "this is a shallow clone; the merge subjects the roadmap needs are not all here."

state="$(gh pr view "$pr" --json state --jq .state)"
[ "$state" = "OPEN" ] || die "pull request #$pr is $state."

# The merge commit lands on the pull request's base, and the roadmap is derived from the tree that
# commit makes, so this has to run somewhere sitting at that base with nothing uncommitted. The test
# is the commit rather than the branch name, because a repository that keeps the base checked out in
# its primary worktree can only offer a detached one: `git worktree add --detach ../spa-land
# origin/main`. Checked before the merge -- a wrong tree found afterwards leaves the merge done and
# the roadmap stale, which is the state this script exists to prevent.
base="$(gh pr view "$pr" --json baseRefName --jq .baseRefName)"
git fetch -q origin "$base" || die "could not fetch origin/$base."
[ "$(git rev-parse HEAD)" = "$(git rev-parse "origin/$base")" ] \
  || die "HEAD is not at origin/$base. Run this from a worktree sitting there, then try again."
git diff --quiet && git diff --cached --quiet \
  || die "this worktree has uncommitted changes; the roadmap refresh would carry them."

# The subject is read by a check, so it is worth saying out loud which one is being used.
if [ -z "$subject" ]; then
  printf 'land: no --subject given, so GitHub will use its own. docs/agents/landing-work.md fixes\n'
  printf '      the two forms this must take, and tools/roadmap.py reads them. Continue? [y/N] '
  # End of input is a no. Under set -e a bare `read` that meets it ends the script with status 1
  # before the refusal below can say why, which is all a session with no terminal would see.
  read -r reply || reply=""
  case "$reply" in [yY]*) ;; *) die "stopped. Pass --subject \"Merge task <id>: ...\"." ;; esac
fi

echo "--- merging #$pr"
merge_args=(--merge)
if [ -n "$subject" ]; then
  merge_args=("${merge_args[@]}" --subject "$subject")
fi
if [ -n "$body_file" ]; then
  merge_args=("${merge_args[@]}" --body-file "$body_file")
fi
gh pr merge "$pr" "${merge_args[@]}"

echo "--- fetching the merge commit for $base"
git fetch origin
git merge --ff-only "origin/$base" \
  || die "could not fast-forward onto origin/$base. Reconcile, then run tools/roadmap.py."

echo "--- deriving the roadmap"
python3 -I tools/roadmap.py

if git diff --quiet -- README.md docs/roadmap.md; then
  echo "land: the generated blocks did not move, so there is nothing to refresh."
  exit 0
fi

git add README.md docs/roadmap.md
git commit -m "Refresh the generated roadmap blocks after landing #$pr

Generated by tools/roadmap.py, which derives the state from the tags, the merge subjects and the
plan. tools/land.sh made this commit because the merge it follows was created on the server and so
never reached .githooks/pre-push.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"

echo "--- pushing the refresh"
git push origin "HEAD:$base"
echo "land: #$pr is in and the roadmap says so."
