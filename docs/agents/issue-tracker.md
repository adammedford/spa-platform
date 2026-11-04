# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Pass a multi-line body on standard input, `--body-file - <<'EOF'`, and a commit message the same way, `git commit -F - <<'EOF'`: bash 3.2, the Bash tool's shell here (#105), fails to parse `"$(cat <<'EOF' … EOF)"` when the text holds an apostrophe, an unmatched `(`, a lone `"` or a lone backtick.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --limit 500 --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Make an issue a sub-issue of a parent**: `gh issue create --parent <parent> ...`, or `gh issue edit <parent> --add-sub-issue <child>` afterwards (`gh` 2.94+). Older `gh`: `gh api --method POST repos/<owner>/<repo>/issues/<parent>/sub_issues -F sub_issue_id=<child-db-id>` (database id, as in **Blocking** below). Without sub-issues, put `Part of #<parent>` at the top of the child body.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

Infer the repo from `git remote -v`; `gh` does this automatically when run inside a clone.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh api --paginate 'repos/{owner}/{repo}/pulls?state=open' --jq '.[] | select(.author_association | IN("OWNER","MEMBER","COLLABORATOR") | not) | {number, title, author: .user.login, author_association, labels: [.labels[].name]}'`.
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (see **Make an issue a sub-issue of a parent**). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`), plus `kind:ticket`, which the Action `.github/workflows/issue-labels.yml` adds to every child (as it puts `kind:parent` on the map) and which stays: leave it on. Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only, the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: read the map's children with their live gates, `gh api repos/<owner>/<repo>/issues/<map>/sub_issues --jq '.[] | select(.state=="open") | {number, blocked_by: .issue_dependencies_summary.blocked_by, assignees: (.assignees|length)}'`, not `gh issue list --search "parent:<map>"`, which returns unrelated issues (for #584 it listed #392 and #63). The frontier is the children that are open, have zero open blockers (`blocked_by` 0, or no open issue in the `Blocked by` line) and have no assignee; first in map order wins. Without sub-issues, read the map's task list instead.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far. The map body has a single writer, the driving session: a ticket resolved by a subagent (parallel research agents, say) has that subagent post its resolution comment, close its ticket and return its gist, and the driving session alone appends the gists to Decisions so far, so no two agents race on the map body.
- **Research findings**: `docs/research/<slug>.md` on a `research/<slug>` branch, pushed and never merged, linked from the ticket's resolution comment. `docs/research/` is integrator-owned by the `*` rule, so a docs-only branch passes the pre-push check. A research brief states the environment facts the agent cannot change (no Docker engine, a connector that failed) so the finding says what was executed and what was read. The finding's shape: a title; header bullets **Ticket** (number, title, map), **Date**, **Branch** and **Method** (the sources and what was not done); an "Answer in brief" section, numbered; one detail section per question; and a closing section that separates what was executed from what was only read, with each source and the date it was read.
- **Glossary and ADRs**: the `domain-modeling` skill edits `GLOSSARY.md` inline, but here the glossary and `docs/adr/` are integrator paths and a commit that changes a steering document names its `Authority:` (`docs/agents/landing-work.md`). A term proposed while charting goes in the map's Notes; the glossary and ADR edits wait for the resolution of the ticket that settles them, and their commit cites that ticket.

## This repository's additions

Moved to `docs/agents/tracker-policy.md` (#412).

## The archive: tickets filed before ADR-0020

Moved to `docs/agents/tracker-policy.md` (#412).
