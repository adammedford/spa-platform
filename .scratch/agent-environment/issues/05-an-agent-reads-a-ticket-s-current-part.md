# 05: An agent working a ticket reads only its current part

**Owner:** integrator
**Blocked by:** None (can start immediately).
**Status:** ready-for-agent

**What to build:** A ticket stays the size of the work it describes. What the next implementer,
reviewer or integrator needs stays in the ticket: the header, What to build, Acceptance, Decisions
taken, the questions with their answers, Answer and Review. The record of how it got there moves to
a history file the ticket links to: grilling records, and the dated status lines each night's
collect step adds under Comments.

Today tickets only grow. The 71 overnight tickets hold 961 KB, and the four largest run 42 to 60 KB,
of which their grilling records are 20 to 36 KB. On 2026-10-17 the run's agents read ticket files
156 times, about 870 KB, roughly 220,000 tokens, and each stage read the same history again.
Answers and Reviews stay, because they are what the next session builds on, so the few tickets
made mostly of them stay large.

- The history file lives outside `issues/`, so the ticket-number check still counts one file per
  ticket, and a citation that names a ticket still resolves to the ticket.
- `docs/agents/issue-tracker.md` states the split: what stays, what moves, and where it goes.
- Every grilling record and dated status line in the overnight tracker moves, without dropping a
  line.
- The overnight workflow's grill and collect steps write their records to the history file. That
  workflow is a local file, ignored by git, so the session that lands this ticket makes the edit in
  Adam's checkout.

- [ ] The convention is in `docs/agents/issue-tracker.md`, and `.github/steering.py` passes over
      the new layout.
- [ ] No overnight ticket holds a grilling record or a dated collect line, and each one that did
      links its history file.
- [ ] A ticket and its history file together hold every line the ticket held before.
- [ ] A grilling in the next overnight run lands in the history file.
