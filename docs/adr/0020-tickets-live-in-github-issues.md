# ADR-0020: Tickets and specs live in GitHub Issues; what outlives a feature stays in the repository

- **Status:** Accepted. **Amended 2026-10-28 by
  [ADR-0032](0032-change-requests-are-issues-and-each-decision-has-one-layer.md):**
  decision 2's four homes become the layers in `docs/adr/README.md` → "Where a decision lives",
  which add the steering documents and a reading order; the other decisions are unchanged.
- **Date:** 2026-10-17
- **Deciders:** integrator
- **Plan ref:** No decision row. `docs/agents/issue-tracker.md`, which this supersedes in part; the
  retrospective on the 2026-10-17 overnight run; agent-environment tickets 04 and 05. Kept to the
  tracker: how a pull request is reviewed before it merges is overnight ticket 70's decision, and
  nothing here touches it.

## Context

A ticket is one markdown file under `.scratch/<feature-slug>/issues/`, tracked by git, holding its
spec, its status line, its blockers, and under `## Comments` everything that happened to it. The
tracker was made tracked for one reason, which `issue-tracker.md` states: a merge body, an ADR or a
contract that cites a ticket by path cites a file every other session and CI can open.

Measured on 2026-10-17, that reason covers a small part of what is tracked. Of some 460 ticket
citations in the tree, about 430 are inside the tracker, citing each other; 28 are outside it, in
ADRs, contracts, `.github/steering.py` and one Terraform comment. Another 88 are in merge bodies.
The tracker itself is 111 files and 1.4 MB, 29% of every byte git tracks.

The rest of what the files hold is workflow state, and keeping it in the repository has costs that
showed up as work:

- **Trunk churn.** 31 of the 368 commits on `main` touch only the tracker. CI has no path filter, so
  each of their pull requests ran the build, the test suite and Terraform for a markdown change.
- **No query.** "Which tickets are ready for an agent?" is a read of every file. The overnight
  collect step of 2026-10-17 spent 229 turns, 50 million input tokens and 26 minutes classifying 22
  open tickets and re-deriving 19 status lines.
- **No separation of spec from history.** A ticket and its record are one file, so the four largest
  run 42 to 60 KB, 20 to 36 KB of it grilling records. The run's agents read ticket files 156 times,
  about 870 KB, and every stage read the same history again.
- **Numbers collide.** Each directory numbers from `01`, so two sessions that cannot see each other's
  branches pick the same number. Overnight ticket 68 was renumbered, a check was added to catch the
  next one, and the convention now says to fetch before filing.
- **The morning copy.** Lanes edit tickets in the integrator's own checkout, the report step copies
  them onto the integration branch, and the checkout's copies are discarded after the merge.

Every agent and script here already speaks to GitHub through `gh`: `tools/land.sh` merges with it,
and last night's lanes made 42 `gh` calls with no auth trouble. `gh` 2.96 creates and edits issues
with `--blocked-by`, `--blocking` and `--parent`, so the relationships the tracker writes as a
`Blocked by:` line exist natively. The repository's Issues are enabled and empty.

## Decision

**1. A ticket is a GitHub issue on this repository.** Its title is the ticket's title, and its body
is the current spec: what to build and the acceptance list, nothing else. Its state is read from
the issue, never from a file: the five triage roles in `docs/agents/triage-labels.md` become labels
of the same names; a blocker is a GitHub blocked-by relationship; a claim is an assignee; resolved
is closed. A feature's spec is an issue too, the one `/to-spec` publishes, and the feature's tickets
are its sub-issues, which is how `/to-tickets` files them. This is the engineering skills' GitHub
tracker as shipped, with no local convention on top.

**2. What outlives a feature stays in the repository.** The contracts, the ADRs, `GLOSSARY.md` and
the merge bodies are unchanged. A spec and its tickets live and die with the feature; a decision a
later session builds on is written into one of those four, and the merge body that lands the work
cites the issue it came from.

**3. The record is the issue's comments.** Grilling records, routing, each night's status notes, the
implementer's answer and the reviewer's findings are comments, in order. The body stays the current
spec. The skills read body and comments together, as their tracker page says; the overnight
workflow's prompts read the body and reach for the comments only where a step needs the history,
which is the split the file format could not give: on 2026-10-17 every stage read everything.

**4. A citation names the issue.** A merge body, an ADR, a contract or a comment cites `#NNN`, which
GitHub links, and `.github/steering.py` holds the citation the way it holds a ticket path today:
every `#NNN` the tree writes must name an issue or pull request of this repository, because the two
share one number sequence and the tree already cites pull requests that way. One
`gh api repos/{owner}/{repo}/issues?state=all` call per run, which lists both, answers it, in CI
through the job's own token. With no network the check says so and passes nothing, as the pre-push
hook does for a missing tool.

**5. The existing directories are an archive.** No new ticket is filed in them. A ticket open on the
day this lands moves to an issue: the issue's first comment names the file it came from, and the
file's `Status:` line becomes `moved to #NNN`, so every existing citation keeps resolving and
nothing is rewritten. Resolved tickets stay as they are. The steering check keeps reading both forms.

**6. An overnight run reports as an issue.** The night's report and its questions are one issue,
`Overnight <date>`, with each question a numbered item; answers are comments on it. The run writes
nothing into the integrator's checkout, and the morning copy ends.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Keep the files; split history out (agent-environment ticket 05) and switch to slug or hash ids | Fixes the reads and the collisions, and leaves the rest: a status change is still a commit, a query is still a read of every file, and the morning copy stays. |
| A git-backed agent tracker such as Beads (`bd`) | Solves ids and queries and stays offline, and every state change is still a commit that CI runs on. A young tool whose current shape would have to be checked before adopting; `gh` is already here and already in every lane. |
| Linear, or another hosted tracker | A second account and token for every agent, a UI this team of one does not need, and citations that leave GitHub, where the pull requests and merge bodies already are. |
| GitHub Projects boards on top of Issues | Nothing here needs a board. Labels, sub-issues and blocked-by answer every query the workflow makes. |

## Consequences

- The switch is made by re-running `/setup-matt-pocock-skills` and choosing GitHub: it rewrites
  `docs/agents/issue-tracker.md` from its GitHub template, updates the `## Agent skills` block in
  `CLAUDE.md`, and creates the triage labels the repository lacks (today it has only `wontfix`).
  Decisions 4 and 5 are then appended to `issue-tracker.md` as this repository's additions, which is
  all that remains of today's page: server-assigned numbers retire its collision and renumbering
  rules. `/wayfinder`'s labels, `wayfinder:map` and `wayfinder:<type>`, are created at the same time.
  `docs/agents/review.md`'s ticket row follows.
- `.github/steering.py` gains the `#NNN` rule and keeps the path rule for the archive.
- CI gains path filters so a change to `.scratch/`, `docs/` or an ADR alone skips the build, the
  tests and Terraform. This stands on its own: it would be right under the files too.
- The overnight workflow's collect step becomes a `gh issue list` on the `ready-for-agent` label;
  lanes label, assign and comment instead of editing files; the report is an issue. The workflow is a
  local file, so the integrator makes these edits.
- Agent-environment ticket 05 is superseded by this decision and closes. Ticket 04 is unaffected.
- An unattended run now depends on GitHub being reachable. A lane that cannot reach it appends what
  it would have written to `.scratch/overnight/log/pending-<date>.jsonl`, which git ignores, and the
  morning session replays it.
- A ticket's answer no longer lands in the commit that lands its code. The merge body's `#NNN` is the
  link, and the merge body is already the record `tools/roadmap.py` reads.
- Issue numbers share one sequence with pull request numbers, so `#NNN` never names two things.
- Proposed for `GLOSSARY.md`, once accepted: **Ticket**, one unit of work with its acceptance, held
  as an issue on this repository and cited as `#NNN`; **Spec**, the statement of a feature's problem,
  solution and acceptance that its tickets are cut from, held the same way and closed when the
  feature lands.
