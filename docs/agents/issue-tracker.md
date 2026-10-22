# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
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
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (see **Make an issue a sub-issue of a parent**). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only, the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children (`gh issue list --state open --limit 500`, scoped to the map's sub-issues / task list), drop any with an open blocker (`issue_dependencies_summary.blocked_by > 0`, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far.
- **Research findings**: `docs/research/<slug>.md` on a `research/<slug>` branch, pushed and never merged, linked from the ticket's resolution comment. `docs/research/` is integrator-owned by the `*` rule, so a docs-only branch passes the pre-push check. A research brief states the environment facts the agent cannot change (no Docker engine, a connector that failed) so the finding says what was executed and what was read.

## This repository's additions

ADR-0020 records the decision and its reasons. What it adds to the page above:

- **A feature is a spec issue with its tickets as sub-issues.** `/to-spec` publishes the spec;
  `/to-tickets` files each ticket with `--parent <spec>`. A blocker is a blocked-by relationship:
  `gh issue create --blocked-by <n>` or `gh issue edit <child> --add-blocked-by <n>` (`gh` 2.94+ has
  the flags; the API route under **Blocking** works on any version). A ticket is open to an agent
  when it carries `ready-for-agent`, every blocker is closed, and nobody is assigned.
- **The shape of a ticket a lane can finish.** Five rules, confirmed on #227:
  1. A ticket has one owner.
  2. A box that needs a sitting is its own ready-for-human ticket, blocked by every ticket that
     builds what it runs.
  3. No box depends on a push or a landing.
  4. Cross-owner work is a spec with one sub-issue per owner in build order, the integrator's
     `Platform.Abstractions` change first. Two halves that must land together stay single-owner on
     two branches, and the landing merges them.
  5. Every ticket carries a `**Touches:**` line of the paths it writes; its owner is derived from
     those paths through `OWNERSHIP.md`.
- **The ticket-shape check holds them where a ticket is written.** `tools/ticket_shape.py` reads one
  issue and fails it, naming the rule, on: `**Touches:**` paths of more than one owner; an
  `**Owner:**` line naming other than exactly the role those paths derive, or other than exactly one
  role where there is no Touches line; under `ready-for-agent`, an open box in the words of a
  sitting or a landing, a `**Session:** foreground` line or a sub-issue; and a "Blocked by #N",
  "after #N" or "once #N lands" with no blocked-by edge to an open #N.
  `.github/workflows/ticket-shape.yml` runs it when an issue carrying `ready-for-agent` is opened,
  edited or labelled, and on a failure swaps that label for `needs-triage` and comments the failing
  rule. The words are a list, and a list misfires. The escape is to reword the box, or to make it a
  ready-for-human ticket blocked by the ticket that builds what it runs; then relabel.
- **The body is the current spec; the comments are the record.** Grilling records, routing, status
  notes, the implementer's answer and the reviewer's findings are comments, in order. Keep the body
  to what to build and the acceptance list, so a reader who needs only the spec reads only the body.
- **A body is rewritten only to carry confirmed decisions.** An answer is confirmed when the human
  wrote it, or confirmed a pre-accepted one on the run's `Overnight <date>` issue; a pre-accepted
  answer alone rewrites nothing. A ticket's body is rewritten by the morning run or by the session
  implementing it; a spec's body only by the session that grilled it. Whoever rewrites names every
  question number applied, preserves the previous body verbatim in a comment headed with the date,
  and ticks or unticks an acceptance box only for work done or undone. The overnight grill
  comments; it never rewrites. A `Confirm` comment is the human's act: no agent session writes one.
- **Current claims are the issue's fields; everything written is dated.** What an issue claims now
  is its state, labels, assignee, sub-issue and blocked-by relationships, corrected in place, and
  its body under the rule above; `docs/agents/triage-labels.md` → "Lifecycle: not labels" says
  which field holds which state. A comment, a merge body and an archived ticket file are records:
  true on the day they were written, and never rewritten. A later truth is a new comment. So a body
  states no status in prose ("remains future work"); where it must say what has landed, it cites the
  merge. Archived overnight ticket 66 carried such a sentence past the merge of its own work,
  because no check reads one.
- **An issue is held open by its own scope.** It closes when what its body asks for has landed,
  been answered or been declined, and its `## Answer` comment says where each part went, naming
  anything declined. A spec's required work is its sub-issues, and the spec closes when they are
  closed. A follow-up, raised by another issue's work, review or out-of-scope list, names where it
  came from on a `**Raised by:** #NNN` line at the top of its body and is filed as neither a
  sub-issue nor a blocker of that issue, so it holds its origin open neither way. A sub-issue means
  "part of" and a blocked-by relationship means "wait for"; no other relation holds an issue open.
- **Cite an issue or a pull request as `#NNN`.** A merge body, an ADR, a contract or a code comment
  cites that way; the two share one number sequence. `.github/steering.py` fails a push over a
  `#NNN` that names neither.
- **Decisions outlive issues.** A decision a later session builds on goes into a contract, an ADR or
  `GLOSSARY.md`, and the merge body cites the issue it came from. Nothing that must survive the
  feature lives only in an issue.
- **The overnight run reports as an issue** titled `Overnight <date>`: the report, then the
  questions as numbered items. Answers are comments on it.
- **A ticket that edits a steering document quotes each paragraph it changes whole, as it will read
  after the edit.** A paragraph is a block blank lines separate in the source: a prose paragraph, a
  list item with its continuation lines, a glossary entry or a fenced block; a table row is quoted
  with its header row, a change to a non-prose file with its smallest enclosing object, and a new
  file whole. A paragraph inserted or removed is quoted with the paragraphs on either side as they
  will read. A sentence spliced into a paragraph reads differently than it did alone: #187 gave its
  edits as old and new sentences, and what landed had an exemption that read as the wrong check's
  and, in the untouched paragraph after an inserted one, an "it" that had lost its referent. The
  implementer changes nothing but the wrapping; when a quoted paragraph has changed on `main` since
  the approval, the session re-quotes it and asks again. The commit cites the human's approval of
  the ticket as `Authority: #<n>` (`docs/agents/landing-work.md`), and an approval covers the body
  as it stood when given. A proposal that a confirmed answer will commit, under
  `## Proposed steering changes` or in a grill's proposals, is quoted the same way.

## The archive: tickets filed before ADR-0020

`.scratch/<feature-slug>/issues/<NN>-<slug>.md` holds every ticket filed before the switch, and is
read-only: no new file is added there, and a ticket that was open when the switch landed carries
`**Status:** moved to #NNN` and continues on GitHub. `.scratch/<feature-slug>/log/` stays ignored.
Resolved tickets keep their `## Answer` and `## Review`, and a merge body, ADR or contract that cites
one still resolves by the rules below, which `.github/steering.py` holds over every tracked file.

A `ticket NN` citation is read off **where it is written**:

- A document inside `.scratch/<feature-slug>/` cites its own directory.
- A document anywhere else cites `.scratch/overnight/issues/`, the plan-wide tracker of that time.
- To cite across directories, name the slug immediately before the citation, as `docs/adr/0018-...md`
  does with "overnight ticket 53".

A dotted number ("task 3.8") is a plan row in `docs/plan/03-workstreams-and-tasks.md`, not a ticket.
