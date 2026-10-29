#!/usr/bin/env bash
# One verdict per launch precondition of the overnight workflow, then its launch arguments.
# Owner: integrator.
#
#     tools/overnight_preflight.sh [--fixture <path>] [--skip <id,...>] <date>
#
# <date> is the YYYY-MM-DD the run ends on, the overnight workflow's `date` argument (an evening
# launch on the 18th uses 2026-10-19). Run it from the Claude Code session that will launch the
# run: line 5 judges the shell that ran it.
#
# --skip names, comma-separated, the tickets the launch plan skips besides the lines' candidates;
# they join the printed arguments' skip. --fixture writes, at <path>, whose directory must exist, the
# input of the overnight harness (.claude/workflows/overnight_harness.mjs, git-ignored), which each
# prep used to build by hand from collect's JSON (#485): `{args, tickets, expect}`, with args the
# printed arguments, their skip joined by the tickets the claims line lists as held, tickets the
# forecast's, verbatim, and expect.integrated the forecast's ready-for-agent ids outside that skip.
# A held ticket is in the fixture's skip and not the printed one: the forecast reads it as claimed,
# so no lane takes it either way, but a rename before launch frees it for the run (#484).
# expect.skippedForBlocker and expect.startsBefore are written empty, as is standingAnswer left out
# of args: the harness reads all three, and they are the human's to fill. A failed forecast writes
# no fixture: one with no tickets would pass the harness and prove nothing.
#
# Why (#378). Before #349's launch the preconditions were checked by hand, from memory notes, over
# about eight separate commands. Each is a refusal or a wasted night when missed: collect refuses a
# main checkout that is not at origin/main or holds a tracked change; a launch by scriptPath runs the
# file in the main checkout, so a stale .claude/workflows/overnight.js runs rules origin has
# replaced; an existing run branch or worktree collides with the run's own; and on 2026-10-19 every
# agent ran zsh because the Claude Code process predated CLAUDE_CODE_SHELL (#129).
#
# The main checkout is the main worktree of the repository this file is in, so a copy in a lane's
# worktree judges the same checkout. The preconditions, one line each, in this order:
#
#   fetch     `git fetch origin --tags` succeeded, and main is on HEAD and neither trails nor leads
#             origin/main. The line names how many commits main trails, and the fast-forward that
#             closes the gap, which is the human's to run.
#   tracked   No tracked change in the main checkout. For each one, whether its content equals
#             origin/main's blob: such a file can be put back to HEAD's copy before the fast-forward,
#             which then brings origin's content back, so it ends exactly as it was.
#   workflow  .claude/workflows/overnight.js equals origin/main's blob: a scriptPath launch runs this
#             file as it stands in the main checkout.
#   run       overnight/<date> exists neither locally nor on origin, and ~/.worktrees/overnight-<date>,
#             the run's worktree root, is absent.
#   shell     The shell that ran this is bash: the Bash tool of the session that runs this, which is
#             the session that launches the run (#129). It is the parent process, as `ps` names it.
#   forecast  origin/main's tools/overnight_collect.py, run with --repo against the main checkout
#             (#377): its summary line, and the ready-for-agent tickets per owner. The copy is
#             origin/main's whole tree, extracted to a temporary directory, because the forecast
#             must use the rules the run will use once main has moved, and the collect tool reads
#             its rules beside itself.
#   demoted   The needs-triage tickets whose ready-for-agent label was removed in the last 24 hours,
#             each a skip candidate: the ticket-shape check demotes a ticket a lane's edit flips
#             (#358, #361), and a demotion the launch plan did not see sends Route at it.
#   claims    Each ticket of the forecast with an open box (openBoxes above 0) held by a local
#             `task/<id>-` branch whose <id> a `Merge part of task <id>` subject in origin/main's
#             history names, with the rename that frees it: git branch -m <branch> hold/<id>-<tip>,
#             the tip's sha cut to 7 (#484). Collect reads any such branch as the ticket's claim,
#             and a part-landed ticket's merged branch stays, because the integration rewrites lane
#             commits and so `git merge-base --is-ancestor` never shows it merged: its open boxes
#             would never get a lane. A held ticket is the human's to free, so the line names each
#             and passes, as demoted does; it fails only when it cannot judge, which is when the
#             forecast failed, since the open boxes come from collect's answer. A branch on origin
#             alone is not listed: the rename moves a local one. Collect reads origin's task branches
#             too, so a held entry whose id origin also has a `task/<id>-` branch for says so: the
#             rename alone does not free that ticket.
#   shape     Every ready-for-agent and needs-triage ticket of the forecast, judged by origin/main's
#             tools/ticket_shape.py under the label it will carry after Route, ready-for-agent
#             (#483). The ticket-shape Action judges a ticket once it carries that label, so a
#             needs-triage ticket Route relabels is demoted mid-run, and the rules sitting-or-landing,
#             shared-commit, foreground and spec fire only under it. Each ticket is read with
#             `gh issue view <n> --json` and its fields FIELDS, its labels rewritten (needs-triage
#             dropped, ready-for-agent added), and piped to `ticket_shape.py -` from the main
#             checkout, where gh resolves the repository. A ticket that fails is a skip candidate,
#             named with its rules. The line fails when the forecast failed, origin/main has no
#             ticket_shape.py, or a ticket could not be read or judged (the check's exit 2): a
#             ticket left unjudged would read as one that passed.
#
# A line is `pass: <name>: <evidence>` or `fail: <name>: <evidence>`. Every line runs whatever the
# ones before it said, so one run shows everything that blocks the launch. Then comes one verdict
# line, `overnight_preflight: pass: ...` or `overnight_preflight: fail: ...`, and last the Workflow
# `args` JSON for the date: repo, date, wt, maxParallel (the number of owners with ready-for-agent
# tickets, at least 1; left out when the forecast failed, so the workflow's default applies) and
# skip (the skip candidates of the demoted and shape lines, then --skip's ids, each once). It leaves out standingAnswer, which the workflow requires: the grant
# is the human's, per launch (ADR-0021 decision 3), so the arguments do not run until they set it.
# The JSON is printed on a fail as well, so the evidence and the arguments are read together.
#
# Exit status: 0 when every line passes, 1 when any fails, 2 for a bad invocation.
#
# What it writes. Nothing in the main checkout's files, index, HEAD, branches or worktrees, and
# nothing on the tracker: git reads run with --no-optional-locks, and the tracked changes are listed
# by status, which honours it, rather than the porcelain diff, which rewrites the index even under it,
# so not even the index's stat cache is refreshed; gh is only asked to list. The one exception is line 1's fetch, which moves the
# remote-tracking refs and adds origin's tags: that is the read AGENTS.md -> "Where the work stands"
# asks for before any work is chosen, and every other line is judged against what it brought. The
# forecast's copy of origin/main goes to a temporary directory outside the checkout, removed on exit.
# The one file it writes is --fixture's, when given, after the arguments, with a line on stderr
# naming it, so stdout still ends on the arguments.
#
# Written for the bash 3.2 macOS ships: no mapfile, no associative arrays, no empty "${array[@]}"
# under set -u. JSON is read and written by python3, which collect needs anyway.
set -u
unset CDPATH

usage() {
  echo "usage: tools/overnight_preflight.sh [--fixture <path>] [--skip <id,...>] <date>   (YYYY-MM-DD, the morning the run ends)" >&2
  exit 2
}

date=""
fixture=""
given_skip=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --fixture)
      [ "$#" -ge 2 ] && [ -n "$2" ] || usage
      fixture="$2"
      shift 2
      ;;
    --skip)
      [ "$#" -ge 2 ] || usage
      given_skip="$2"
      shift 2
      ;;
    -*) usage ;;
    *)
      [ -z "$date" ] || usage
      date="$1"
      shift
      ;;
  esac
done
case "$date" in
  [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]) ;;
  *) usage ;;
esac
# --skip is ids separated by commas, each a plan-row id or issue-<n>: no space, no empty id.
if [ -n "$given_skip" ]; then
  case ",$given_skip," in
    *,,*) usage ;;
  esac
  case "$given_skip" in
    *[!A-Za-z0-9._,-]*) usage ;;
  esac
fi
# The fixture's directory must exist now, so a bad path is refused before anything runs.
if [ -n "$fixture" ]; then
  fixture_dir="${fixture%/*}"
  [ "$fixture_dir" = "$fixture" ] && fixture_dir=.
  [ -d "$fixture_dir" ] || usage
fi

here="${0%/*}"
[ "$here" = "$0" ] && here=.
here="$(cd "$here" && pwd -P)" || { echo "overnight_preflight: cannot enter ${0%/*}" >&2; exit 2; }

scratch="$(mktemp -d "${TMPDIR:-/tmp}/overnight-preflight.XXXXXX")" ||
  { echo "overnight_preflight: cannot make a temporary directory" >&2; exit 2; }
trap 'rm -rf "$scratch"' EXIT

# The main checkout: the first entry `git worktree list` gives is the main worktree
# (https://git-scm.com/docs/git-worktree#_list_output_format).
git --no-optional-locks -C "$here" worktree list --porcelain > "$scratch/worktrees" 2>/dev/null ||
  { echo "overnight_preflight: $here is not in a git checkout" >&2; exit 2; }
read -r first < "$scratch/worktrees"
repo="${first#worktree }"
[ -n "$repo" ] && [ "$repo" != "$first" ] ||
  { echo "overnight_preflight: git names no main worktree for $here" >&2; exit 2; }

g() { git --no-optional-locks -C "$repo" "$@"; }

failed=""
row() {
  # row <pass|fail> <name> <evidence>
  echo "$1: $2: $3"
  [ "$1" = pass ] || failed="$failed $2"
}

# --- fetch ------------------------------------------------------------------------------------------
if g fetch origin --tags > "$scratch/fetch" 2>&1; then
  fetched="git fetch origin --tags succeeded"
  fetch_ok=true
else
  last=""
  while IFS= read -r line; do [ -n "$line" ] && last="$line"; done < "$scratch/fetch"
  fetched="git fetch origin --tags failed (${last:-no output}), so origin/main is the last one fetched"
  fetch_ok=false
fi
origin_main=""
origin_main="$(g rev-parse -q --verify refs/remotes/origin/main 2>/dev/null)" || origin_main=""
main="$(g rev-parse -q --verify refs/heads/main 2>/dev/null)" || main=""
head_ref="$(g symbolic-ref -q HEAD 2>/dev/null)" || head_ref="a detached HEAD"
if [ -z "$origin_main" ] || [ -z "$main" ]; then
  missing="origin/main"
  [ -n "$main" ] || missing="main"
  row fail fetch "$fetched; $missing is not a ref here, so nothing is compared"
else
  trails="$(g rev-list --count "$main..$origin_main" 2>/dev/null)" || trails=""
  leads="$(g rev-list --count "$origin_main..$main" 2>/dev/null)" || leads=""
  if [ -z "$trails" ] || [ -z "$leads" ]; then
    row fail fetch "$fetched; git could not count the commits between main and origin/main"
  else
    state="main trails origin/main by $trails commits and leads it by $leads (main ${main:0:7}, origin/main ${origin_main:0:7})"
    if [ "$trails" -gt 0 ] && [ "$leads" -eq 0 ]; then
      state="$state; the fast-forward is: git -C $repo merge --ff-only origin/main"
    fi
    if [ "$head_ref" != refs/heads/main ]; then
      state="$state; the checkout is on ${head_ref#refs/heads/}, not main"
    fi
    if $fetch_ok && [ "$trails" -eq 0 ] && [ "$leads" -eq 0 ] && [ "$head_ref" = refs/heads/main ]; then
      row pass fetch "$fetched; $state"
    else
      row fail fetch "$fetched; $state"
    fi
  fi
fi

# --- tracked ----------------------------------------------------------------------------------------
# The blob a path has on origin/main, or "" when origin/main has no such file.
origin_blob() { g rev-parse -q --verify "refs/remotes/origin/main:$1" 2>/dev/null || true; }
# The blob the main checkout's copy of a path would have, or "" when it has none.
checkout_blob() {
  if [ -f "$repo/$1" ] || [ -L "$repo/$1" ]; then g hash-object -- "$1" 2>/dev/null || echo unreadable
  fi
}
# The listing is the one collect's dirty-start guard reads, `status --porcelain --untracked-files=no`
# (.claude/workflows/overnight.js, Collect), so this line fails exactly when that guard would refuse.
# It is status, not `git diff HEAD`: the porcelain diff refreshes the index's stat cache and writes it
# back even under --no-optional-locks (seen with git 2.54), so a tracked file whose mtime alone moved
# would rewrite the main checkout's index. status honours the flag and leaves the index unwritten:
# https://git-scm.com/docs/git#Documentation/git.txt-GITOPTIONALLOCKS
# Each -z record is "XY <path>"; with --no-renames no record carries a second path
# (https://git-scm.com/docs/git-status#_porcelain_format_version_1).
if ! g status --porcelain=v1 -z --untracked-files=no --no-renames > "$scratch/changed" 2> "$scratch/changed.err"; then
  row fail tracked "git could not list the tracked changes against HEAD, so none were judged"
else
  count=0
  same=""
  differ=""
  while IFS= read -r -d '' record; do
    path="${record:3}"
    count=$((count + 1))
    if [ "$(checkout_blob "$path")" = "$(origin_blob "$path")" ]; then
      same="$same $path"
    else
      differ="$differ $path"
    fi
  done < "$scratch/changed"
  if [ "$count" -eq 0 ]; then
    row pass tracked "no tracked change in $repo"
  else
    evidence="$count tracked changes in $repo"
    [ -n "$same" ] && evidence="$evidence; equal to origin/main's blob:$same (put HEAD's copy back with git show HEAD:<path> > <path>, and the fast-forward restores this content)"
    [ -n "$differ" ] && evidence="$evidence; differing from origin/main's blob:$differ (the human's to keep or drop: collect refuses a dirty start)"
    row fail tracked "$evidence"
  fi
fi

# --- workflow ---------------------------------------------------------------------------------------
script=.claude/workflows/overnight.js
want="$(origin_blob "$script")"
have="$(checkout_blob "$script")"
if [ -z "$want" ]; then
  row fail workflow "origin/main has no $script"
elif [ -z "$have" ]; then
  row fail workflow "$repo/$script is missing; origin/main's is ${want:0:7}"
elif [ "$have" = "$want" ]; then
  row pass workflow "$script equals origin/main's blob ${want:0:7}"
else
  row fail workflow "$script is ${have:0:7}, origin/main's is ${want:0:7}: a scriptPath launch would run the checkout's"
fi

# --- run --------------------------------------------------------------------------------------------
branch="overnight/$date"
wt="${HOME:?HOME is unset}/.worktrees/overnight-$date"
problems=""
if g show-ref --verify -q "refs/heads/$branch"; then problems="$problems; $branch exists locally"; fi
if g ls-remote --exit-code --heads origin "refs/heads/$branch" > /dev/null 2>&1; then
  problems="$problems; $branch exists on origin"
else
  status=$?
  # 2 is ls-remote's "no matching ref" with --exit-code; anything else is a failed read.
  [ "$status" -eq 2 ] || problems="$problems; origin could not be asked for $branch (git ls-remote exit $status)"
fi
if [ -e "$wt" ] || [ -L "$wt" ]; then problems="$problems; $wt exists"; fi
if [ -z "$problems" ]; then
  row pass run "$branch exists neither locally nor on origin, and $wt is absent"
else
  row fail run "${problems#; }"
fi

# --- shell ------------------------------------------------------------------------------------------
parent="$(ps -p "$PPID" -o comm= 2>/dev/null)" || parent=""
name="${parent##*/}"
name="${name#-}"   # a login shell's name starts with a dash
if [ "$name" = bash ]; then
  row pass shell "the shell that ran this is ${parent} (pid $PPID)"
else
  row fail shell "the shell that ran this is ${parent:-unknown} (pid $PPID), not bash: run this from the session that will launch; if its Bash tool is not bash, restart Claude Code so CLAUDE_CODE_SHELL takes effect (#129)"
fi

# --- forecast ---------------------------------------------------------------------------------------
copy="$scratch/origin-main"
forecast_ok=false
mkdir "$copy"
if ! g archive --format=tar refs/remotes/origin/main > "$scratch/origin-main.tar" 2> "$scratch/archive.err" ||
   ! tar -xf "$scratch/origin-main.tar" -C "$copy" 2>> "$scratch/archive.err"; then
  row fail forecast "origin/main's tree could not be copied out, so collect did not run"
elif [ ! -f "$copy/tools/overnight_collect.py" ]; then
  row fail forecast "origin/main has no tools/overnight_collect.py"
else
  python3 -I "$copy/tools/overnight_collect.py" --json --repo "$repo" \
    > "$scratch/collect.json" 2> "$scratch/collect.err"
  status=$?
  summary=""
  last=""
  while IFS= read -r line; do
    [ -n "$line" ] && last="$line"
    case "$line" in overnight_collect:*) summary="$line" ;; esac
  done < "$scratch/collect.err"
  if [ "$status" -ne 0 ]; then
    row fail forecast "origin/main's overnight_collect.py --repo exited $status: ${last:-no output}"
  elif owners="$(python3 -I -c '
import json, sys
tickets = json.load(open(sys.argv[1]))["tickets"]
counts = {}
for ticket in tickets:
    if ticket["status"] == "ready-for-agent":
        counts[ticket["owner"]] = counts.get(ticket["owner"], 0) + 1
print(", ".join(f"{owner} {n}" for owner, n in sorted(counts.items())) or "none")
' "$scratch/collect.json" 2>/dev/null)"; then
    forecast_ok=true
    row pass forecast "${summary:-collect printed no summary line}; ready-for-agent by owner: $owners"
  else
    row fail forecast "origin/main's overnight_collect.py printed JSON with no readable tickets"
  fi
fi
$forecast_ok || : > "$scratch/collect.json"

# --- demoted ----------------------------------------------------------------------------------------
# Each needs-triage issue's timeline, every page of it, judged here rather than by a --jq filter so
# the filter is under test. The timeline lists each label event with the label and when it happened:
# https://docs.github.com/en/rest/issues/timeline#list-timeline-events-for-an-issue
# --slurp wraps the pages in one array (`gh api --help`), as tools/land.sh reads its listings.
mkdir "$scratch/timelines"
if ! (cd "$repo" && gh issue list --state open --label needs-triage --limit 500 --json number) \
       > "$scratch/triage.json" 2> "$scratch/triage.err" ||
   ! numbers="$(python3 -I -c '
import json, sys
print(" ".join(str(int(issue["number"])) for issue in json.load(open(sys.argv[1]))))
' "$scratch/triage.json" 2>/dev/null)"; then
  row fail demoted "gh could not list the needs-triage issues, so no demotion was read"
else
  unread=""
  for number in $numbers; do
    (cd "$repo" && gh api --paginate --slurp "repos/{owner}/{repo}/issues/$number/timeline?per_page=100") \
      > "$scratch/timelines/$number.json" 2>/dev/null || unread="$unread #$number"
  done
  if [ -n "$unread" ]; then
    row fail demoted "gh could not read the timeline of$unread, so its demotions are unread"
  elif python3 -I -c '
import datetime, json, pathlib, sys
listing, timelines, collect, out = sys.argv[1:]
now = datetime.datetime.now(datetime.timezone.utc)
ids = {}
try:
    ids = {t["issue"]: t["id"] for t in json.load(open(collect))["tickets"]}
except (ValueError, KeyError, TypeError):
    pass  # the forecast failed: name each candidate by its issue
numbers = [int(issue["number"]) for issue in json.load(open(listing))]
lines = []
for number in sorted(numbers):
    pages = json.loads(pathlib.Path(timelines, f"{number}.json").read_text())
    removed = [datetime.datetime.fromisoformat(event["created_at"].replace("Z", "+00:00"))
               for page in pages for event in page
               if event.get("event") == "unlabeled"
               and (event.get("label") or {}).get("name") == "ready-for-agent"]
    recent = [at for at in removed if now - at <= datetime.timedelta(hours=24)]
    if recent:
        name = ids.get(number) or f"issue-{number}"
        lines.append(f"{name}\t#{number}, removed {max(recent):%Y-%m-%dT%H:%M:%SZ}\n")
pathlib.Path(out).write_text("".join(lines))
print(len(numbers))
' "$scratch/triage.json" "$scratch/timelines" "$scratch/collect.json" "$scratch/candidates" \
      > "$scratch/listed" 2>/dev/null; then
    read -r listed < "$scratch/listed"
    list=""
    while IFS=$'\t' read -r id what; do list="$list; $id ($what)"; done < "$scratch/candidates"
    if [ -z "$list" ]; then
      row pass demoted "$listed needs-triage tickets; none lost ready-for-agent in the last 24 hours"
    else
      row pass demoted "$listed needs-triage tickets; these lost ready-for-agent in the last 24 hours and are skip candidates: ${list#; }"
    fi
  else
    : > "$scratch/candidates"
    row fail demoted "the needs-triage timelines could not be read as label events"
  fi
fi

# --- claims -----------------------------------------------------------------------------------------
# The local task branches with their tips, and origin/main's subjects, judged against collect's answer
# in python so the matching is under test. An id is compared whole, so issue-1 is not issue-10.
if ! $forecast_ok; then
  row fail claims "the forecast failed, so no ticket's open boxes are known and no claim was judged"
elif ! g for-each-ref --format='%(refname:short) %(objectname)' refs/heads/task/ > "$scratch/task-branches" 2>/dev/null ||
     ! g for-each-ref --format='%(refname:lstrip=3)' refs/remotes/origin/task/ > "$scratch/origin-task-branches" 2>/dev/null ||
     ! g log --format=%s refs/remotes/origin/main > "$scratch/subjects" 2>/dev/null; then
  row fail claims "git could not list the task branches or origin/main's subjects, so no claim was judged"
elif held="$(python3 -I -c '
import json, re, sys
collect, branches, remote, subjects, repo, out = sys.argv[1:]
merged = set()
for subject in open(subjects):
    match = re.match(r"Merge part of task ([^\s:]+)", subject)
    if match:
        merged.add(match.group(1))
tips = [line.split() for line in open(branches) if line.strip()]
# Collect reads origin/task/ as well (overnight_collect.branches), so a same-id branch there still claims.
on_origin = [line.strip() for line in open(remote) if line.strip()]
held = []
ids = []
for ticket in sorted(json.load(open(collect))["tickets"], key=lambda t: t["issue"]):
    tid, number, boxes = ticket["id"], ticket["issue"], ticket["openBoxes"]
    if boxes <= 0 or tid not in merged:
        continue
    for name, sha in tips:
        if name.startswith(f"task/{tid}-"):
            entry = (f"{tid} (#{number}, {boxes} open boxes): "
                     f"git -C {repo} branch -m {name} hold/{tid}-{sha[:7]}")
            also = [other for other in on_origin if other.startswith(f"task/{tid}-")]
            if also:
                entry += f" (origin/{also[0]} claims it too, which the rename does not free)"
            held.append(entry)
            if tid not in ids:
                ids.append(tid)
with open(out, "w") as listing:
    listing.write("".join(f"{tid}\n" for tid in ids))
print("; ".join(held))
' "$scratch/collect.json" "$scratch/task-branches" "$scratch/origin-task-branches" "$scratch/subjects" "$repo" \
    "$scratch/held" 2>/dev/null)"; then
  if [ -z "$held" ]; then
    row pass claims "no ticket with an open box is held by a local task branch whose id a \`Merge part of task <id>\` subject on origin/main names"
  else
    row pass claims "these tickets have open boxes and are held by a local task branch whose id a \`Merge part of task <id>\` subject on origin/main names, so collect reads them as claimed; free each by renaming its branch: $held"
  fi
else
  row fail claims "collect's answer could not be read for open boxes, so no claim was judged"
fi
[ -f "$scratch/candidates" ] || : > "$scratch/candidates"
[ -f "$scratch/held" ] || : > "$scratch/held"

# --- shape ------------------------------------------------------------------------------------------
# The fields are tools/ticket_shape.py's FIELDS, the JSON its `-` reads.
: > "$scratch/shaped"
mkdir "$scratch/issues"
if ! $forecast_ok; then
  row fail shape "the forecast failed, so no ticket was judged"
elif [ ! -f "$copy/tools/ticket_shape.py" ]; then
  row fail shape "origin/main has no tools/ticket_shape.py, so no ticket was judged"
elif ! judged="$(python3 -I -c '
import json, sys
tickets = json.load(open(sys.argv[1]))["tickets"]
print(" ".join(str(int(t["issue"])) for t in tickets
               if t["status"] in ("ready-for-agent", "needs-triage")))
' "$scratch/collect.json" 2>/dev/null)"; then
  row fail shape "the forecast's tickets could not be read, so no ticket was judged"
else
  unread=""
  unjudged=""
  count=0
  for number in $judged; do
    count=$((count + 1))
    issue="$scratch/issues/$number"
    if ! (cd "$repo" && gh issue view "$number" --json number,body,labels,subIssuesSummary,blockedBy) \
           > "$issue.json" 2>/dev/null ||
       ! python3 -I -c '
import json, sys
issue = json.load(open(sys.argv[1]))
issue["labels"] = [label for label in issue["labels"]
                   if label["name"] not in ("needs-triage", "ready-for-agent")]
issue["labels"].append({"name": "ready-for-agent"})
print(json.dumps(issue))
' "$issue.json" > "$issue.ready.json" 2>/dev/null; then
      unread="$unread #$number"
      continue
    fi
    (cd "$repo" && python3 -I "$copy/tools/ticket_shape.py" - < "$issue.ready.json") > "$issue.out" 2>&1
    case $? in
      0) ;;
      1) echo "$number" >> "$scratch/shaped" ;;
      *) unjudged="$unjudged #$number" ;;
    esac
  done
  # Each failed ticket as "<id>\t#<n>: <rules>", named by collect's id as the demoted line names it.
  if python3 -I -c '
import json, pathlib, re, sys
collect, issues, shaped, out = sys.argv[1:]
ids = {t["issue"]: t["id"] for t in json.load(open(collect))["tickets"]}
lines = []
for number in [int(n) for n in open(shaped).read().split()]:
    rules = []
    for line in pathlib.Path(issues, f"{number}.out").read_text().splitlines():
        found = re.match(r"^ticket-shape: ([a-z-]+): ", line)
        if found and found.group(1) not in ("note", "pass", "fail") and found.group(1) not in rules:
            rules.append(found.group(1))
    name = ids.get(number) or "issue-%d" % number
    lines.append("%s\t#%d: %s\n" % (name, number, ", ".join(rules) or "no rule named"))
pathlib.Path(out).write_text("".join(lines))
' "$scratch/collect.json" "$scratch/issues" "$scratch/shaped" "$scratch/shape-candidates" 2>/dev/null; then
    list=""
    while IFS=$'\t' read -r id what; do list="$list; $id ($what)"; done < "$scratch/shape-candidates"
    if [ -z "$list" ]; then
      found="none fails"
    else
      found="these fail and are skip candidates: ${list#; }"
    fi
    problems=""
    [ -n "$unread" ] && problems="$problems; gh could not read$unread"
    [ -n "$unjudged" ] && problems="$problems; ticket_shape.py judged nothing for$unjudged"
    if [ -z "$problems" ]; then
      row pass shape "$count ready-for-agent and needs-triage tickets judged as ready-for-agent; $found"
    else
      row fail shape "$count ready-for-agent and needs-triage tickets${problems}, so those are unjudged; of the rest, $found"
    fi
  else
    : > "$scratch/shape-candidates"
    row fail shape "ticket_shape.py's verdicts could not be read"
  fi
fi
[ -f "$scratch/shape-candidates" ] || : > "$scratch/shape-candidates"

# --- verdict and arguments --------------------------------------------------------------------------
if [ -z "$failed" ]; then
  echo "overnight_preflight: pass: every precondition holds. The arguments follow; add \"standingAnswer\": true or false, yours to grant for this launch (ADR-0021 decision 3)."
else
  echo "overnight_preflight: fail:$failed. The arguments follow, for when those lines pass; add \"standingAnswer\": true or false, yours to grant for this launch (ADR-0021 decision 3)."
fi
python3 -I -c '
import json, os, sys
repo, date, wt, collect, candidates, shaped, held, given, ok, fixture = sys.argv[1:]
args = {"repo": repo, "date": date, "wt": wt}
if ok == "true":
    owners = {t["owner"] for t in json.load(open(collect))["tickets"] if t["status"] == "ready-for-agent"}
    args["maxParallel"] = max(1, len(owners))
named = [line.split("\t")[0] for path in (candidates, shaped) for line in open(path) if line.strip()]
given = [tid for tid in given.split(",") if tid]
args["skip"] = list(dict.fromkeys(named + given))
print(json.dumps(args))
if fixture:
    if ok != "true":
        print("overnight_preflight: no harness fixture written: the forecast failed", file=sys.stderr)
        sys.exit(0)
    tickets = json.load(open(collect))["tickets"]
    held = [line.strip() for line in open(held) if line.strip()]
    skip = list(dict.fromkeys(named + held + given))
    integrated = [t["id"] for t in tickets if t["status"] == "ready-for-agent" and t["id"] not in skip]
    written = {"args": {**args, "skip": skip}, "tickets": tickets,
               "expect": {"integrated": integrated, "skippedForBlocker": [], "startsBefore": []}}
    with open(fixture, "w") as out:
        json.dump(written, out, indent=1)
        out.write("\n")
    print(f"overnight_preflight: wrote the harness fixture {os.path.abspath(fixture)}: {len(tickets)} "
          f"tickets, {len(integrated)} expected to integrate, {len(skip)} skipped; add standingAnswer to "
          "its args, and skippedForBlocker and startsBefore to its expect, before the harness runs it",
          file=sys.stderr)
' "$repo" "$date" "$wt" "$scratch/collect.json" "$scratch/candidates" "$scratch/shape-candidates" \
  "$scratch/held" "$given_skip" "$forecast_ok" "$fixture" ||
  { echo "overnight_preflight: fail: the arguments could not be written"; exit 1; }

[ -z "$failed" ]
