#!/bin/bash
# One-off, run once on Adam's Mac before the first sequence launch (the plan of 2026-10-21, #213 and #109).
#
#   bash .scratch/sequence/unblock.sh --repo ~/Projects/spa-platform --wt ~/.worktrees/sequence-2026-10-22 \
#     [--transcripts <dir> [<dir> ...]] [--no-push]
#
# 1. #213: the live bytes that exist only in the main checkout, which git ignores, become the first commit of
#    task/issue-213-track-companion-and-skills (Q42): .claude/workflows/overnight_harness.mjs as overnight_tests.mjs,
#    .claude/workflows/fixtures/2026-10-20-tickets.json and .claude/skills/wiz/SKILL.md, plus Q36's three .gitignore
#    lines. The branch is made in a worktree under --wt and pushed with the hook running; agents do the rest of #213.
# 2. #109: counts the Edit refusals "File has not been read yet" in the transcripts' tool results (not in prompts or
#    #109's own text, which also carry the phrase). None: ticks #109's open box, answers and closes it.
# It writes no tracked file in the main checkout. Written for the bash 3.2 macOS ships.
set -eu

repo=""; wt=""; push=1; transcripts=()
while [ $# -gt 0 ]; do
  case "$1" in
    --repo) repo="$2"; shift 2 ;;
    --wt) wt="$2"; shift 2 ;;
    --no-push) push=0; shift ;;
    --transcripts) shift; while [ $# -gt 0 ] && [ "${1#--}" = "$1" ]; do transcripts+=("$1"); shift; done ;;
    *) echo "unblock: unknown argument $1" >&2; exit 2 ;;
  esac
done
[ -n "$repo" ] && [ -n "$wt" ] || { echo "unblock: --repo and --wt are required" >&2; exit 2; }
case "$wt" in "$repo"|"$repo"/*) echo "unblock: --wt must lie outside the repository" >&2; exit 2 ;; esac
umask 022

# ---------------------------------------------------------------- 1. #213's live bytes
branch=task/issue-213-track-companion-and-skills
dest="$wt/issue-213"
live_tests="$repo/.claude/workflows/overnight_harness.mjs"
live_fixture="$repo/.claude/workflows/fixtures/2026-10-20-tickets.json"
live_skill="$repo/.claude/skills/wiz/SKILL.md"
for f in "$live_tests" "$live_fixture" "$live_skill"; do
  [ -f "$f" ] || { echo "unblock: $f is missing; nothing was written" >&2; exit 1; }
done
if [ -n "$(git -C "$repo" status --porcelain --untracked-files=no)" ]; then
  echo "unblock: the main checkout has tracked changes; nothing was written" >&2; exit 1
fi
git -C "$repo" fetch -q origin
if git -C "$repo" rev-parse -q --verify "refs/remotes/origin/$branch" > /dev/null; then
  echo "unblock: origin already has $branch; step 1 skipped"
else
  mkdir -p "$wt"
  git -C "$repo" worktree add -q -b "$branch" "$dest" origin/main
  mkdir -p "$dest/.claude/workflows/fixtures" "$dest/.claude/skills/wiz"
  cp "$live_tests" "$dest/.claude/workflows/overnight_tests.mjs"
  cp "$live_fixture" "$dest/.claude/workflows/fixtures/2026-10-20-tickets.json"
  cp "$live_skill" "$dest/.claude/skills/wiz/SKILL.md"
  # The pre-push hook refuses a tracked file only its owner can read (#205); the live copies may be 0600.
  chmod go+r "$dest/.claude/workflows/overnight_tests.mjs" "$dest/.claude/workflows/fixtures/2026-10-20-tickets.json" "$dest/.claude/skills/wiz/SKILL.md"
  python3 -I - "$dest/.gitignore" <<'PY'
import sys
path = sys.argv[1]
lines = open(path).read().split("\n")
anchor = "!.claude/workflows/*.js"
if anchor not in lines:
    sys.exit("unblock: .gitignore has no line " + anchor)
add = ["!.claude/workflows/overnight_tests.mjs", "!.claude/workflows/fixtures/", "!.claude/skills/"]
at = lines.index(anchor) + 1
lines[at:at] = [a for a in add if a not in lines]
open(path, "w").write("\n".join(lines))
PY
  git -C "$dest" add .gitignore .claude/workflows/overnight_tests.mjs .claude/workflows/fixtures/2026-10-20-tickets.json .claude/skills/wiz/SKILL.md
  git -C "$dest" commit -q -F - <<'MSG'
Task issue-213: the live bytes of the overnight companion, its fixture and the wiz skill

The first commit #213's Q42 asks for: overnight_harness.mjs as it lives in the main checkout, renamed
overnight_tests.mjs (Q27), the fixture of 2026-10-20 as it is (Q28), and .claude/skills/wiz/SKILL.md as it is (Q26),
with the three .gitignore lines of Q36. Nothing in them is edited here; the header rename, the three wiz corrections,
the CI step, the ADR and the glossary entry follow as their own commits. Before fast-forwarding the main checkout
after the landing, cmp each live file against this commit's blob.

Ticket: #213
MSG
  sha="$(git -C "$dest" rev-parse HEAD)"
  if [ "$push" = 1 ]; then git -C "$dest" push -q -u origin "$branch"; fi
  echo "unblock: #213's live bytes are $sha on $branch$([ "$push" = 1 ] && echo ', pushed' || echo ', not pushed (--no-push)')"
  echo "unblock: before 'git merge --ff-only origin/main' after #213 lands, run in $repo:"
  echo "  cmp .claude/workflows/overnight_harness.mjs <(git show $sha:.claude/workflows/overnight_tests.mjs)"
  echo "  cmp .claude/workflows/fixtures/2026-10-20-tickets.json <(git show $sha:.claude/workflows/fixtures/2026-10-20-tickets.json)"
  echo "  cmp .claude/skills/wiz/SKILL.md <(git show $sha:.claude/skills/wiz/SKILL.md)"
  echo "  then, once the fast-forward is done: rm .claude/workflows/overnight_harness.mjs"
fi

# ---------------------------------------------------------------- 2. #109's evidence
if [ ${#transcripts[@]} -eq 0 ]; then
  echo "unblock: no --transcripts given; #109 left open"
  exit 0
fi
hits="$(python3 -I - "${transcripts[@]}" <<'PY'
import json, pathlib, sys
needle = "File has not been read yet"
hits = []
files = 0
for root in sys.argv[1:]:
    for path in sorted(pathlib.Path(root).expanduser().rglob("*.jsonl")):
        files += 1
        for n, line in enumerate(path.open(errors="replace"), 1):
            try:
                event = json.loads(line)
            except ValueError:
                continue
            content = (event.get("message") or {}).get("content")
            if not isinstance(content, list):
                continue
            for part in content:
                if not isinstance(part, dict) or part.get("type") != "tool_result":
                    continue
                body = part.get("content")
                text = body if isinstance(body, str) else " ".join(
                    p.get("text", "") for p in body or [] if isinstance(p, dict))
                text = text.strip()
                if text.startswith("<tool_use_error>"):
                    text = text[len("<tool_use_error>"):].lstrip()
                if text.startswith(needle):
                    hits.append(f"{path}:{n}")
if files == 0:
    sys.exit("unblock: no .jsonl transcript under the --transcripts directories")
print(len(hits))
for h in hits:
    print(h, file=sys.stderr)
PY
)"
if [ "$hits" != 0 ]; then
  echo "unblock: $hits Edit refusals \"File has not been read yet\" (listed above); #109 stays open"
  exit 0
fi
body="$(mktemp)"; answer="$(mktemp)"
gh issue view 109 --repo adammedford/spa-platform --json body --jq .body > "$body"
python3 -I - "$body" <<'PY'
import sys
path = sys.argv[1]
text = open(path).read()
open(path, "w").write(text.replace("- [ ] The next run's transcripts", "- [x] The next run's transcripts", 1))
PY
gh issue edit 109 --repo adammedford/spa-platform --body-file "$body"
{
  echo "## Answer"
  echo
  echo "The HOUSE line (open a file with Read before Edit) is on main in \`.claude/workflows/overnight.js\`, tracked since PR #214. The transcripts of the runs since it landed hold no tool result beginning \"File has not been read yet\", counted by \`.scratch/sequence/unblock.sh\` over ${#transcripts[@]} transcript directories. Both boxes are met."
} > "$answer"
gh issue comment 109 --repo adammedford/spa-platform --body-file "$answer"
gh issue close 109 --repo adammedford/spa-platform --reason completed
rm -f "$body" "$answer"
echo "unblock: #109 ticked, answered and closed"
