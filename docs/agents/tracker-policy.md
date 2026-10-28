# Tracker policy

This repository's rules for its GitHub issue tracker. `docs/agents/issue-tracker.md` holds the
mechanics, which the setup skill writes and a re-run of it rewrites; this file holds what the
repository adds to them, and no setup skill writes it (#412).

## This repository's additions

ADR-0020 records the decision and its reasons. What it adds to `docs/agents/issue-tracker.md`:

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
  role where there is no Touches line or it says `none`; under `ready-for-agent`, an open box in
  the words of a sitting or a landing, an open box that requires another ticket's commit on this
  ticket's branch, a `**Session:** foreground` line or a sub-issue; and a "Blocked by #N", "after
  #N" or "once #N lands" where #N is an open issue with no blocked-by edge. A #N that is a closed
  issue or a pull request is stale prose: the check prints it as a note and fails nothing. A
  "Blocked by" list that names the ticket itself ("and this ticket", or its own #N) describes
  another ticket's wait and is not read.
  `.github/workflows/ticket-shape.yml` runs it when an issue carrying `ready-for-agent` is opened,
  edited or labelled, and on a failure swaps that label for `needs-triage` and comments the failing
  rule. The words are a list, and a list misfires. The escape is to reword the box, or to make it a
  ready-for-human ticket blocked by the ticket that builds what it runs; then relabel.
- **The body is the current spec; the comments are the record.** Grilling records, routing, status
  notes, the implementer's answer and the reviewer's findings are comments, in order. Keep the body
  to what to build and the acceptance list, so a reader who needs only the spec reads only the body.
- **The human's account is `@adammedford`.** A `Confirm` comment, and the `ready-for-agent` label
  on a ticket, count only when this account wrote them (`CODING_STANDARDS.md` → "An authorisation
  is read from its author"). `.github/steering.py` refuses an `Authority: <URL>` whose comment
  another account wrote, and `tools/overnight_collect.py` lists a ticket whose `ready-for-agent`
  another account last applied as `needs-triage`; both read the account from this line, and the
  repository's owner while it is absent.
- **A body is rewritten only to carry confirmed decisions.** An answer is confirmed when the human
  wrote it, or confirmed a pre-accepted one on the run's `Overnight <date>` issue; a pre-accepted
  answer alone rewrites nothing. A ticket's body is rewritten by the morning run or by the session
  implementing it; a spec's body only by the session that grilled it. Whoever rewrites names every
  question number applied, preserves the previous body verbatim in a comment headed with the date,
  and ticks or unticks an acceptance box only for work done or undone. The overnight grill
  comments; it never rewrites. A `Confirm` comment is the human's act: no agent session writes one.
- **Current claims are the issue's fields; everything written is dated.** What an issue claims now
  is its state, labels, assignee, sub-issue and blocked-by relationships, corrected in place, and
  its body under the rule above; "Lifecycle: not labels" below says
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

## Lifecycle: not labels

The labels in `docs/agents/triage-labels.md` say what an issue needs. Where it stands is held by GitHub's own fields, never by
a label or a line in the body (ADR-0020):

| State | Held by | Read from |
| --- | --- | --- |
| Claimed | An assignee: `gh issue edit <n> --add-assignee @me`, a session's first write | `assignees` |
| Blocked | An open blocked-by relationship (`docs/agents/issue-tracker.md` → Blocking) | `issue_dependencies_summary.blocked_by` above zero, from `gh api repos/{owner}/{repo}/issues/<n>`; `gh issue view --json` has no such field |
| Resolved | Closed, after an `## Answer` comment saying where the work or the answer is | `state` is `CLOSED` |

- **Declined** is resolved too: closed as not planned (`gh issue close <n> --reason "not planned"`),
  labelled `wontfix`, with a comment saying why.
- **Partial** is not a state. Work done in part leaves the issue open, and its `## Answer` carries
  an `Outstanding:` line naming what is left. The assignee stays until that part lands and is then
  removed, so the rest can be claimed: **Frontier query** in `docs/agents/issue-tracker.md` passes
  over an assigned issue.
- **A follow-up is not a state of its origin.** It is an issue of its own, and holds the issue it
  came from open neither way ("An issue is held open by its own scope", above).

The archive's `**Status:**` lines (`claimed`, `blocked`, `resolved`, `closed`, `partial`, `moved to #NNN`)
are dated records of the tickets filed before ADR-0020. Read them as history; never write one, and
never take one for the current state of a moved ticket, whose issue holds it.

## Change requests

**What it is for.** A change request asks for a change on an integrator path that the asking
session's own ticket does not authorise. What one is: `GLOSSARY.md` → "Change request".

**Filing.** Anyone may file one. Search the open `change-request` issues first, and when one already
asks for the change, add a blocked-by edge to it instead of filing another (#409). Label it
`change-request` and `ready-for-human`, never `needs-triage`, and title it in plain prose with no id
prefix. Its body is a `**Touches:**` line, a `**Rows:**` line when it serves plan rows, then
`## The gap` and `## The change`, with every steering paragraph it changes quoted whole (above). The
ticket that needs it is blocked by it, and one request may block many tickets as one ticket may wait
on many requests; a request that nothing waits on carries `**Raised by:** #NNN` and blocks nothing.
It may be filed from a pre-accepted answer, because filing decides nothing (ADR-0021 as amended).

**Queued filing.** An overnight session that cannot reach GitHub queues the request as a pending
`create` line, and the morning files it after a title search and comments its number on the blocked
ticket; the line's shape is `morning.js`'s.

**Waiting.** The session that needs the change adds the edge, merges what it has with an
`Outstanding:` line citing the request, drops its assignee once that part lands, and stops (#408).

**Deciding.** The human decides a request in a decision comment on the request issue itself. Its
first line is exactly one of `Decision: accept`, `Decision: accept with changes`,
`Decision: decline`, or `Decision: accept as drafted <URL of the draft comment>`. Its second line is
`ADR: yes` or `ADR: no`, on every decision, a decline included. `accept as drafted` covers the draft
as it stood when the decision was posted: if the draft comment was edited after that (its
`updated_at` is later than the decision's `created_at`), the decision does not cover it, and the
integrator asks again. A comment that departs from this form is not a decision comment: the
integrator lane treats the request as undecided and relabels it `ready-for-human`. No parser reads
the form; the integrator lane and the reviewer do.

A scripted session never posts a comment whose first line begins `Decision:`. Its draft is headed
`## Draft decision`, and its first two lines read `Proposed decision: …` and `Proposed ADR: yes|no`.
A foreground session may post a decision comment only on Adam's words given in that session, and the
comment's last line says so ("Posted from a foreground session on Adam's words in it."). Every
session posts as `@adammedford`, so this is a convention the shared account cannot enforce, as the
rule that no agent session writes a `Confirm` is (above): the prompts and the reviewer guard it.

A `Confirm` on the Overnight issue never authorises a change request's edit; only the request's
decision comment does. `.github/steering.py` reads only a cited comment's author, so it cannot tell
the two apart, and the reviewer holds a commit whose `Authority:` is a `Confirm` on a change request.

**Landing.** The human relabels a decided request `ready-for-agent` when a lane may make the edit.
The commit that makes it cites the decision comment's URL as its `Authority:`
(`docs/agents/landing-work.md` → "A commit that changes a steering document"), and
`Authority: #<n>` naming a change request is refused. With no decision comment, the integrator
relabels the request `ready-for-human` and stops. When the decision says `ADR: no`, the merge body
states the why and cites the decision comment (#413).

**Closing.** A request closes when its edit lands. Declined, it is closed as not planned, labelled
`wontfix`, with a comment saying why; withdrawn, it is closed as not planned. A request decided but
not yet landed is open.

**The archived requests.** The files under `contracts/changes/` are the requests filed before
change requests were issues. They are read-only records, cited by their id, and no file is added
there. Their `**Status:**` lines, `moved to #NNN` included, are history.

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
