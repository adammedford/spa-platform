#!/usr/bin/env bash
# Lands a reviewed pull request: check its review record, merge, regenerate the roadmap, push, and
# delete the branches the landing finished.
# Owner: integrator.
#
#     tools/land.sh <pr-number> [--subject "<merge subject>"] [--body-file <path>]
#                   [--unreviewed "<reason>"]
#
# Run it from a worktree sitting at the pull request's base with nothing uncommitted, because the
# roadmap is derived from the tree the merge commit makes:
#
#     git worktree add --detach ../spa-land origin/main
#
# --subject is the merge subject a check reads. --body-file names the merge body, which reaches the
# merge commit followed by the review trailer below: a file, or a pipe -- a named pipe, or a
# process substitution such as `--body-file <(...)` -- but never standard input, `-` included,
# because with no --subject standard input answers the confirmation. The body says what was
# verified and how, and it carries the `Outstanding:` line and the `Outstanding test ids:` line
# when a row landed without being finished.
# Both merge subject forms and what the body owes are in docs/agents/landing-work.md -> "Land it".
#
# It merges only a head a review record clears (#67). A review record is one line a review leaves on
# the pull request, `Reviewed <40-hex head> against <40-hex base>: merge` (or `: hold`), whose form
# is in docs/agents/review.md -> "Landing or holding". The latest record naming the pull request's
# current head must say merge, or nothing merges; tools/review_record.py, beside this script, reads
# the records and says which. The merge is then pinned to that head with --match-head-commit, so a
# push that lands while this runs makes GitHub refuse the merge rather than take an unreviewed head.
# The merge body ends with `Reviewed: <head> <record URL>`, which this script writes, not its caller.
#
# --unreviewed "<reason>" is the one escape: for a red main that cannot wait, or no session free to
# review. It merges without reading the records, still pinned to the head it saw, and ends the body
# with `Unreviewed: <reason>`. That review stays open until a record naming the merged head says
# merge, and no gate tag is cut over it: docs/agents/landing-work.md -> "Gates".
#
# It never passes --admin. gh's --admin merges "a pull request that does not meet requirements",
# which here would skip the required CI checks as well: https://cli.github.com/manual/gh_pr_merge
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
# see that nothing else rode in with it. Then, for a merge into main, tools/prune_branches.py beside
# this script deletes the branches the merge finished and lists the ones it keeps (#71); its
# companion, tools/prune_branches_tests.py, runs this script through a merge to see it happen.
#
# Written for the bash 3.2 that ships with macOS.

set -euo pipefail

die() { printf 'land: %s\n' "$*" >&2; exit 1; }

pr=""
subject=""
body_file=""
unreviewed=""
newline=$'\n'
while [ $# -gt 0 ]; do
  case "$1" in
    --subject) [ $# -ge 2 ] || die "--subject needs a value."; subject="$2"; shift 2 ;;
    # Checked here rather than at the merge: a path that turns out to be unreadable after the merge
    # leaves the merge done and the roadmap stale, which is what this script exists to prevent.
    --body-file)
      [ $# -ge 2 ] || die "--body-file needs a value."
      # Standard input is refused under every name, `-` included, although gh's own --body-file
      # reads `-` as standard input (https://cli.github.com/manual/gh_pr_merge): with no --subject,
      # this script reads its confirmation from standard input, and a body there would race the
      # answer. A deliberate narrowing (#72). `-ef` compares device and inode, so /dev/stdin,
      # /dev/fd/0 and any path naming the file or pipe standard input is are all caught: bash's manual,
      # https://www.gnu.org/software/bash/manual/html_node/Bash-Conditional-Expressions.html.
      if [ "$2" = - ] || [ "$2" -ef /dev/stdin ]; then
        die "--body-file: $2 is standard input, which answers the confirmation asked when no --subject is given; pass a file, a named pipe or <(...)."
      fi
      # A pipe is accepted as well as a regular file: a named pipe, or the /dev/fd/N that a caller's
      # `--body-file <(...)` hands over, both of which gh reads (#72). -p follows the path to what
      # it names, so a /dev/fd/N whose descriptor this process did not inherit names nothing and is
      # refused here, the way a missing path is. The descriptor stays open until the body is read
      # below, because nothing in this script closes one it inherited; tools/land_tests.py reads
      # the body through one.
      [ -f "$2" ] || [ -p "$2" ] || die "--body-file: $2 is not a file or a pipe."
      [ -r "$2" ] || die "--body-file: $2 is not readable."
      # Made absolute here, because the script changes to the repository root before the merge and
      # a relative path checked from a subdirectory would then name a different file, or none.
      # CDPATH is cleared because an exported one sends `cd` to another directory of the same name,
      # and `cd` prints it, so the path would name another file over two lines: bash's manual, cd,
      # https://www.gnu.org/software/bash/manual/html_node/Bourne-Shell-Builtins.html. `--` ends
      # each command's options, so a name starting with a hyphen is a name: POSIX utility syntax
      # guideline 10, https://pubs.opengroup.org/onlinepubs/9799919799/basedefs/V1_chap12.html#tag_12_02.
      # tools/land_tests.py has both.
      body_file="$(CDPATH= cd -- "$(dirname -- "$2")" && pwd)/$(basename -- "$2")"; shift 2 ;;
    # The reason is written into the merge body as one `Unreviewed:` line, so it has to be there,
    # and it has to be one line.
    --unreviewed)
      [ $# -ge 2 ] || die "--unreviewed needs a reason, which the merge body carries."
      case "$2" in
        *"$newline"*) die "--unreviewed: the reason must be one line; the merge body carries it as one." ;;
        *[![:space:]]*) ;;
        *) die "--unreviewed: the reason is blank. Say why this merges without a review record." ;;
      esac
      unreviewed="$2"; shift 2 ;;
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

# The record reader is the one beside this script, the version this script was written against.
# Found before the cd below, because a relative $0 names it from the caller's directory.
here="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"

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

# The head the merge is pinned to, read once. Every later step uses this value: reading the head again
# for the merge would pin whatever was pushed meanwhile, which no record has cleared. A head that is
# not a full sha is refused because --match-head-commit is what makes the pin hold.
head="$(gh pr view "$pr" --json headRefOid --jq .headRefOid)"
full_sha='^[0123456789abcdef]{40}$'
[[ $head =~ $full_sha ]] || die "gh reported '$head' as the head of #$pr, which is not a full commit sha."

if [ -n "$unreviewed" ]; then
  trailer="Unreviewed: $unreviewed"
else
  # Both listings whole: --paginate follows every page and --slurp wraps the pages in one array, so
  # a record on page two is read (`gh api --help`). {owner} and {repo} are filled from this
  # repository's remote, as for `gh pr view`. The reader prints the clearing record's URL, or says
  # why nothing clears the head, on standard error above the line below.
  echo "--- reading the review records of #$pr at $head"
  comments="$(gh api "repos/{owner}/{repo}/issues/$pr/comments" --paginate --slurp)"
  reviews="$(gh api "repos/{owner}/{repo}/pulls/$pr/reviews" --paginate --slurp)"
  record="$(printf '{"comments": %s, "reviews": %s}\n' "$comments" "$reviews" \
    | python3 -I "$here/review_record.py" "$head")" \
    || die "no review record clears #$pr at $head, so it does not merge. An agent session that wrote none of its commits reviews it and posts the record; docs/agents/review.md -> Landing or holding."
  trailer="Reviewed: $head $record"
fi

# The trailer is the body's last line, after one blank line. $(<) drops the file's trailing newlines,
# so the gap is always one blank line however the file ended.
body="$trailer"
if [ -n "$body_file" ]; then
  text="$(< "$body_file")"
  [ -z "$text" ] || body="$(printf '%s\n\n%s' "$text" "$trailer")"
fi

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
# gh sends --match-head-commit as the merge's expectedHeadOid, and only when it is not empty (cli/cli
# pkg/cmd/pr/merge/merge.go and http.go), which is why an empty head is refused above. GitHub's schema
# calls it the "OID that the pull request head ref must match to allow merge; if omitted, no check is
# performed": https://docs.github.com/en/graphql/reference/input-objects#mergepullrequestinput
merge_args=(--merge --match-head-commit "$head")
if [ -n "$subject" ]; then
  merge_args=("${merge_args[@]}" --subject "$subject")
fi
merge_args=("${merge_args[@]}" --body "$body")
gh pr merge "$pr" "${merge_args[@]}"
if [ -n "$unreviewed" ]; then
  echo "land: #$pr merged unreviewed. Its review stays open until a record naming $head says merge."
fi

echo "--- fetching the merge commit for $base"
git fetch origin
git merge --ff-only "origin/$base" \
  || die "could not fast-forward onto origin/$base. Reconcile, then run tools/roadmap.py."

echo "--- deriving the roadmap"
python3 -I tools/roadmap.py

# A merge that lands a row as done finishes its claim branch, and a merged overnight pull request
# finishes its integration branch: docs/agents/landing-work.md -> "Land it" says each is deleted
# then, and this is where (#71). The cleanup beside this script reads which rows the roadmap now
# calls landed, so it runs once the merge is fetched, from this checkout, which sits at origin/main.
# It deletes only what its rules allow and lists every branch it keeps and why. A cleanup that stops
# leaves finished branches behind, not a wrong merge, so it warns rather than failing a landing that
# has already happened.
prune_finished() {
  [ "$base" = main ] || return 0
  echo "--- deleting the branches this landing finished"
  python3 -I "$here/prune_branches.py" --delete \
    || printf 'land: the branch cleanup stopped, so finished branches may remain. Run\n      tools/prune_branches.py --delete from a checkout at origin/main.\n' >&2
}

if git diff --quiet -- README.md docs/roadmap.md; then
  echo "land: the generated blocks did not move, so there is nothing to refresh."
  prune_finished
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
prune_finished
