#!/usr/bin/env bash
# Lands a reviewed pull request: check its review record, merge, regenerate the roadmap, push, and
# delete the branches the landing finished.
# Owner: integrator.
#
#     tools/land.sh <pr-number> --body-file <path> [--subject "<merge subject>"]
#                   [--unreviewed "<reason>"]
#
# Run it from a worktree sitting at the pull request's base with nothing uncommitted, because the
# roadmap is derived from the tree the merge commit makes:
#
#     git worktree add --detach ../spa-land origin/main
#     git worktree remove ../spa-land        # done for you when the landing succeeds
#
# That worktree is disposable (#490). Once the merge, the roadmap refresh and its push have all
# succeeded, a landing run from a linked worktree that is detached and clean, untracked files
# included, removes it as its last act and names it. From a worktree on a branch, or one with an
# untracked file, it leaves it and prints the `git worktree remove` that removes it; a landing that
# fails anywhere leaves it for inspection. The repository's own working tree is never removed.
#
# --subject is the merge subject a check reads. --body-file names the merge body, which reaches the
# merge commit followed by the review trailer below: a file, or a pipe -- a named pipe, or a
# process substitution such as `--body-file <(...)` -- but never standard input, `-` included,
# because with no --subject standard input answers the confirmation. The body says what was
# verified and how, and it carries the `Outstanding:` line and the `Outstanding test ids:` line
# when a row landed without being finished. So it is required: a landing with no --body-file, or
# one whose body is empty or only whitespace, is refused before anything is asked of gh (#552),
# where before it merged with the trailer alone.
# Both merge subject forms and what the body owes are in docs/agents/landing-work.md -> "Land it".
#
# It merges only a head a review record clears (#67). A review record is one line a review leaves on
# the pull request, `Reviewed <40-hex head> against <40-hex base>: merge` (or `: hold`), whose form
# is in docs/agents/review.md -> "Landing or holding". The latest record naming the pull request's
# current head must say merge, or nothing merges; tools/review_record.py, beside this script, reads
# the records and says which, run as `review_record.py --pr <number> --head <head>` so the gh calls
# that list them live in the reader alone (#488). The merge is then pinned to that head with
# --match-head-commit, so a push that lands while this runs makes GitHub refuse the merge rather
# than take an unreviewed head.
# The merge body ends with `Reviewed: <head> <record URL>`, which this script writes, not its caller.
#
# It merges only a description whose every `Closes #n` line names a ticket some non-merge commit
# between the base and that head names in its message, as `#n` or as `issue-n`, because the merge
# closes each one (#566). A refusal names each #n no commit names, and --unreviewed does not lift it.
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
# this script deletes the branches the merge finished and counts the ones it keeps by reason (#71,
# #524), and when it deleted any, this script's last line names the file of the commands that
# restore them (#115).
# Just before the cleanup it names the repository's main checkout, says whether it is behind the
# merge, and prints the fast-forward to run there when docs/agents/landing-work.md allows one; it
# never moves that checkout itself (#281). Its companion, tools/prune_branches_tests.py, runs this
# script through a merge to see both happen.
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
      # after this loop, because nothing in this script closes one it inherited; tools/land_tests.py
      # reads the body through one.
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

# The body is required, and read here, once, before anything is asked of gh (#552, R3 of #98).
# Until then a missing --body-file, an empty file or an empty pipe merged with the trailer alone,
# although docs/agents/landing-work.md -> "Land it" says the body says what was verified and how.
# Read once because a pipe cannot be read twice, and the text is kept for the merge. $(<) drops the
# file's trailing newlines; the blank test is the one --unreviewed's reason gets, so a body of
# newlines, spaces and tabs is as empty as no file.
[ -n "$body_file" ] \
  || die "--body-file is required: the merge body says what was verified and how (docs/agents/landing-work.md -> Land it). Pass --body-file <path>."
text="$(< "$body_file")"
case "$text" in
  *[![:space:]]*) ;;
  *) die "--body-file: $body_file is empty or only whitespace, and the merge body says what was verified and how (docs/agents/landing-work.md -> Land it)." ;;
esac

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

# A `Closes #n` line in the description closes ticket n when this merges into the default branch:
# https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/linking-a-pull-request-to-an-issue.
# So each n has to be a ticket the branch did work for (#566). On 2026-10-29 #536's branch was
# rebuilt without e2fc266, the one commit naming #352, and its description still closed #352, whose
# work change request #551 still carried; only a sub-agent's reading caught it. Every n a Closes
# line names must appear as `#n`, or as the token `issue-n`, in the message (subject, body or
# trailer) of a non-merge commit between the base and the pinned head. `issue-n` is how an
# overnight lane's task commit names a ticket with no plan row, `Task issue-<n>: ...` in
# .claude/workflows/overnight.js, where only the lane's merge carries `Ticket: #n`; without it the
# check refused 13 of the 23 Closes lines of #529, on the overnight route (decided on #566:
# https://github.com/adammedford/spa-platform/issues/566#issuecomment-6511060750). A ticket whose
# body has a `**Plan row:** X` line gets task id X, so its lane commit, `Task X: ...`, names it
# only where the lane wrote #n in the body; one that did not is refused, failing closed, and the
# refusal's remedy applies.
# A merge is left out because its message names what it joins, which a rebuild can keep after the
# commit that did the work is gone. It runs with --unreviewed too: a review record clears a head,
# not a description, which can change after it.
#
# A Closes line is read in the form docs/agents/landing-work.md -> "Land it" gives it and
# .claude/workflows/morning.js reads, one `Closes #n` to a line, with the keyword in any case, as
# GitHub reads it, and whatever follows the number, such as the carriage return that ends each line
# of a description edited on the web. n is every digit after the `#`. `#n` and `issue-n` are
# matched in a message only where no digit follows, so #35 is not found inside #352 nor issue-35
# inside issue-352, and `issue-n` only where no letter, digit or underscore comes before it. The
# head's commits are read only when there is a Closes line, and fetched by sha when this clone
# lacks them, the way actions/checkout fetches a pull request's commit from GitHub:
# https://github.com/actions/checkout.
description="$(gh pr view "$pr" --json body --jq .body)"
closes_line='^[[:space:]]*[Cc][Ll][Oo][Ss][Ee][Ss][[:space:]]+#([0-9]+)'
closes=""
while IFS= read -r line; do
  if [[ $line =~ $closes_line ]]; then closes="$closes #${BASH_REMATCH[1]}"; fi
done <<< "$description"
if [ -n "$closes" ]; then
  { git cat-file -e "$head^{commit}" 2>/dev/null || git fetch -q origin "$head"; } \
    && messages="$(git log --no-merges --format=%B "origin/$base..$head")" \
    || die "could not read the commits of #$pr up to $head, so the tickets its Closes lines name cannot be checked."
  unnamed=""
  for ticket in $closes; do
    named="($ticket|(^|[^[:alnum:]_])issue-${ticket#\#})([^0-9]|\$)"
    [[ $messages =~ $named ]] || unnamed="$unnamed $ticket"
  done
  [ -z "$unnamed" ] \
    || die "the description of #$pr closes$unnamed, which no non-merge commit between origin/$base and $head names as #n or issue-n, so it does not merge. Name each in the commit that did its work; a ticket the branch only advances takes a Ticket: #n line instead (docs/agents/landing-work.md -> Land it)."
fi

if [ -n "$unreviewed" ]; then
  trailer="Unreviewed: $unreviewed"
else
  # The reader's --pr form lists the pull request's comments and reviews itself, every page of each
  # (#488), and judges the head passed with --head: the one this script pinned above, not whatever
  # a second read would report. It prints the clearing record's URL, or says on standard error, above
  # the line below, why nothing clears the head (status 1) or why it could not read the records
  # (status 2: a gh call failed, or what came back was not GitHub's shape).
  echo "--- reading the review records of #$pr at $head"
  status=0
  record="$(python3 -I "$here/review_record.py" --pr "$pr" --head "$head")" || status=$?
  case "$status" in
    0) ;;
    1) die "no review record clears #$pr at $head, so it does not merge. An agent session that wrote none of its commits reviews it and posts the record; docs/agents/review.md -> Landing or holding." ;;
    *) die "could not read the review records of #$pr, so it does not merge; the reader says why above." ;;
  esac
  trailer="Reviewed: $head $record"
fi

# The trailer is the body's last line, after one blank line. The text was read above with its
# trailing newlines dropped, so the gap is always one blank line however the file ended.
body="$(printf '%s\n\n%s' "$text" "$trailer")"

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
# It deletes only what its rules allow and counts the branches it keeps under each reason, which
# its --verbose lists one by one (#524). A cleanup that stops leaves finished branches behind, not a
# wrong merge, so it warns rather than failing a landing that has already happened.
#
# A cleanup that deleted anything ends on a line naming the file it wrote the restore lines to, in
# the words below, which tools/prune_branches.py keeps as RESTORE_FILE_LINE; the two change
# together. Its output is held until it ends so that line can be read back, and this script's own
# last line names the file again (#115). The first landing to run the cleanup deleted 34 branches,
# its session read this script through `tail -20`, and 14 of the restore lines went with the
# terminal. A cleanup that cannot write the file stops deleting, prints its lines and exits 1, so
# the last line is then the warning below, and the file, if any, is not named.
#
# Before the cleanup, so that line stays last, comes the main checkout (#281). The merge leaves the
# repository's own working tree where it was, behind origin/main, and only the human moves it:
# docs/agents/landing-work.md -> "The main checkout is shared", which allows one move there, exactly
# `git merge --ff-only origin/main`, while HEAD is `main` and no tracked file has changes. So this
# reports and never acts. The checkout is the worktree holding `main` in `git worktree list`, or,
# when none holds it, the first one listed, which is the repository's own (git-worktree(1), LIST
# OUTPUT FORMAT): https://git-scm.com/docs/git-worktree. It is named with whether it is behind, and
# the fast-forward is printed, to run as it stands, only when that rule allows it and it would move
# something. Its status is read with --no-optional-locks, so reading it writes nothing there, not
# even a refreshed index: https://git-scm.com/docs/git#Documentation/git.txt---no-optional-locks.
# Nothing here fails the landing, which has already happened: what cannot be read is said so.
name_main_checkout() {
  local listing line path="" branch="" tip="" first=1 target="" target_branch="" target_head=""
  local merged behind changes plural
  listing="$(git worktree list --porcelain)" \
    || { echo "land: git could not list the worktrees, so the main checkout is not named."; return 0; }
  # Each worktree is a block of lines ended by a blank one; the here-string adds the last blank.
  while IFS= read -r line; do
    case "$line" in
      "worktree "*) path="${line#worktree }"; branch=""; tip="" ;;
      "HEAD "*) tip="${line#HEAD }" ;;
      "branch refs/heads/"*) branch="${line#branch refs/heads/}" ;;
      bare) path="" ;;
      "")
        if [ -n "$path" ]; then
          if [ "$branch" = main ] && [ "$target_branch" != main ]; then
            target="$path"; target_branch="$branch"; target_head="$tip"
          elif [ "$first" = 1 ] && [ -z "$target" ]; then
            target="$path"; target_branch="$branch"; target_head="$tip"
          fi
        fi
        first=0; path="" ;;
    esac
  done <<< "$listing$newline"
  if [ -z "$target" ]; then
    echo "land: no worktree of this repository holds main, and its own is bare, so there is no main checkout to name."
    return 0
  fi
  if [ "$target_branch" != main ]; then
    echo "land: the main checkout, $target, is not on main (it is on ${target_branch:-a detached HEAD}), so no fast-forward is given; moving it is yours."
    return 0
  fi
  # Under set -e an unguarded read that fails would end the script here, before the cleanup.
  merged="$(git rev-parse "origin/$base" 2>/dev/null)" \
    || { echo "land: git could not read origin/main, so the main checkout, $target, is named with no command."; return 0; }
  if [ "$target_head" = "$merged" ]; then
    echo "land: the main checkout, $target, is at origin/main; there is nothing to fast-forward."
    return 0
  fi
  if ! git merge-base --is-ancestor "$target_head" "origin/$base" 2>/dev/null; then
    echo "land: the main checkout, $target, has commits origin/main lacks, so it cannot fast-forward and no command is given."
    return 0
  fi
  behind="$(git rev-list --count "$target_head..origin/$base" 2>/dev/null)" \
    || { echo "land: the main checkout, $target, is behind origin/main, and git could not count by how much, so no command is given."; return 0; }
  plural=s; [ "$behind" != 1 ] || plural=""
  if ! changes="$(git --no-optional-locks -C "$target" status --porcelain --untracked-files=no)"; then
    echo "land: the main checkout, $target, is $behind commit$plural behind origin/main, and git could not read its status, so no command is given."
    return 0
  fi
  if [ -n "$changes" ]; then
    echo "land: the main checkout, $target, is $behind commit$plural behind origin/main and has changes to tracked files, so no command is given."
    return 0
  fi
  echo "land: the main checkout, $target, is $behind commit$plural behind origin/main. To bring it up, run:"
  printf '      git -C %q merge --ff-only origin/main\n' "$target"
}

restore_file_line="prune_branches: these commands are also in "
pruned=0
restore_file=""
prune_finished() {
  [ "$base" = main ] || return 0
  name_main_checkout
  echo "--- deleting the branches this landing finished"
  listing="$(python3 -I "$here/prune_branches.py" --delete)" || pruned=$?
  [ -z "$listing" ] || printf '%s\n' "$listing"
  last="${listing##*"$newline"}"
  case "$last" in
    "$restore_file_line"?*) restore_file="${last#"$restore_file_line"}" ;;
  esac
}

# The worktree the landing ran in is disposable once the landing has happened (#490): eighteen were
# left behind by earlier landings before this. It is removed only when it is a linked worktree, on
# a detached HEAD, with nothing uncommitted and no untracked file -- the one `git worktree add
# --detach` made for this landing. A worktree on a branch is somebody's work, and an untracked file
# is something nobody has looked at, so either is left and the command that removes it is printed.
# The repository's own working tree is never removed, and git cannot remove it anyway. Ignored
# files do not count, and `git worktree remove` deletes them with the tree without --force:
# https://git-scm.com/docs/git-worktree#_commands. It runs after the cleanup, which needs a checkout
# at origin/main to read the roadmap from, and nothing here fails the landing, which has happened.
release_worktree() {
  local git_dir common at reason changes
  git_dir="$(CDPATH= cd -- "$(git rev-parse --git-dir)" && pwd -P)" \
    && common="$(CDPATH= cd -- "$(git rev-parse --git-common-dir)" && pwd -P)" \
    || { echo "land: git could not name this worktree's git directories, so $root is left as it is."; return 0; }
  [ "$git_dir" != "$common" ] || return 0
  # The printed command names the main checkout when the common directory is its .git.
  at="$common"
  case "$at" in */.git) at="${at%/.git}" ;; esac
  if git symbolic-ref -q HEAD >/dev/null; then
    reason="it is on the branch $(git symbolic-ref --short HEAD)"
  elif ! changes="$(git status --porcelain)"; then
    reason="git could not read its status"
  elif [ -n "$changes" ]; then
    reason="it has uncommitted or untracked files"
  else
    # Out of the tree before removing it, so nothing after this runs in a directory that is gone.
    # A cd that fails is a reason to keep the tree, not an exit under set -e after the landing.
    if cd -- "$common" && git worktree remove -- "$root"; then
      echo "land: removed the worktree this landing ran in, $root, which was detached and clean."
      # The tree is gone whatever the prune says, so a failed prune is named, not reported as a
      # tree that stays.
      git worktree prune \
        || echo "land: git worktree prune failed after the removal; run git -C $(printf %q "$at") worktree prune."
      return 0
    fi
    reason="git refused to remove it"
  fi
  echo "land: the worktree this landing ran in, $root, stays: $reason. To remove it, run:"
  printf '      git -C %q worktree remove %q\n' "$at" "$root"
}

# The landing's last act and its last lines: the branch cleanup, then the worktree, then what the
# cleanup left to say, so the line naming the restore file stays last (#115).
finish_landing() {
  prune_finished
  release_worktree
  [ "$pruned" -eq 0 ] \
    || printf 'land: the branch cleanup stopped, so finished branches may remain. Run\n      tools/prune_branches.py --delete from a checkout at origin/main.\n' >&2
  [ -z "$restore_file" ] \
    || echo "land: the commands that restore what the cleanup deleted are in $restore_file"
}

if git diff --quiet -- README.md docs/roadmap.md; then
  echo "land: the generated blocks did not move, so there is nothing to refresh."
  finish_landing
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
finish_landing
