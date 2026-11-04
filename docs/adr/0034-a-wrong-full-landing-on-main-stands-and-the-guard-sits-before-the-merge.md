# ADR-0034: A wrong full landing on main stands, and the guard sits before the merge

- **Status:** Accepted
- **Date:** 2026-10-29
- **Deciders:** integrator, on Adam's answers to #388 (confirmed as Q195 to Q203 on #442) and #476
  Q1 to Q3
- **Plan ref:** No decision row. Proposed on #388 ("Proposed glossary / ADR",
  https://github.com/adammedford/spa-platform/issues/388#issuecomment-6426438825), confirmed as
  Q203 on #442 (#388 Q9), drafted under #457 and bound to that draft's text by #476 Q3. The rule it
  guards is #332 D7, as #476 Q2 amended it.

## Context

fa96201 and 98e2317 merged P2-1 and P2-2 as `Merge task` while part tickets of both rows were open,
breaching #332 D7; the roadmap counts both landed. The breach was mechanical: overnight.js's
integrate step chose the subject from the lane's own ticket (`impl.complete`), and
`tools/landing_subjects.py` judged a full landing only against the issue on the merge's own
`Ticket:` line. The relation "ticket X is part of row P2-n" lived only in prose in an issue body,
which no tool read.

## Decision

A landing subject on main is never corrected by rebuilding the trunk, nor by a demotion form or an
issue-reading pass in the roadmap derivation; the roadmap stays a function of tags, merge subjects
and the plan, in which a full landing wins for good, and the record is corrected in prose (the
ticket's Answer).

Prevention sits before the merge: a full-landing subject naming a plan row is held while an open
part ticket declares that row, and the merge of a row's last open part names the row once the
carrier has landed it in part and never when the row is already landed whole; #332 D7 holds the
rule. A part ticket declares itself in its Plan row line as
`**Plan row:** none (part of <row id>[, ... and <row id>][; prose])`, the row ids read up to the
first `;` or `)`. `tools/landing_subjects.py` holds such a subject in the check, and its
`--subject-for` mode gives overnight.js's integrate step the subject before the merge is written
(#457).

## Alternatives rejected

- A trunk rebuild: moves every cited sha.
- A "Reopen task" subject: a second non-monotonic reading that `landing_subjects.py`,
  `prune_branches.py`, `overnight_collect.py` and `merged_both_ways` would all have to learn.
- Reading open issues in `roadmap.py`: network in the pre-push hook, and a `--check` that flaps with
  the tracker.

## Why an ADR

Hard to reverse (subjects on main are permanent, and every tool reading the roadmap relies on its
monotonic reading); surprising without context (a reader sees P2-1 counted landed while a part
ticket is open and will want to "fix" the count); a real trade-off (three genuine alternatives,
each rejected for a stated reason).

## Consequences

- fa96201 and 98e2317 stand. #388 closes on an Answer that records that both breached D7 by
  mechanism, and the roadmap's count of P2-1 and P2-2 is left to the derivation.
- The guard landed under #457 (`3fac4ea`): `tools/landing_subjects.py` holds a full-landing subject
  while an open part ticket declares its row, and `--subject-for` gives the integrate step the
  subject. The rule runs forward only: a row already landed whole is never named again.
- `GLOSSARY.md` → "Carrier" reads as #476 Q1 replaced it, in the commit that carries this ADR;
  **Plan row** and **Part ticket** landed under #457 (`dcae207`).
- #332 D7 is amended by #476 Q2, confirmed 2026-10-28; its body carries the amended sentence once
  the morning applies it.

## Evidence

- #388's grill and Answer sheet, confirmed as Q195 to Q203 on #442:
  https://github.com/adammedford/spa-platform/issues/442#issuecomment-6432297940
- The draft this file is written from, on #457:
  https://github.com/adammedford/spa-platform/issues/457#issuecomment-6474200593
- #476's Confirm, Q1 to Q3:
  https://github.com/adammedford/spa-platform/issues/476#issuecomment-6474906480
- The finding on #385 that raised #388:
  https://github.com/adammedford/spa-platform/pull/385#issuecomment-6401040787
