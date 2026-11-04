# ADR-0023: A lane's code may rest on a pre-accepted answer; a steering document changes only on a cited authority

- **Status:** Accepted. **Amended 2026-10-28 by
  [ADR-0032](0032-change-requests-are-issues-and-each-decision-has-one-layer.md):**
  decision 2's set leaves out the three setup-written guides and no longer exempts an added change
  request; decision 3 counts a decision comment as a confirmed answer and never a change-request
  issue's number; decision 4 is replaced: a proposed steering change is filed as a change request;
  the other decisions are unchanged.
- **Date:** 2026-10-18
- **Deciders:** integrator
- **Plan ref:** No decision row. Amends ADR-0021 decision 1. Decided in the grill of the
  retrospective of the overnight run of 2026-10-18 (Q1–Q16, recorded on #99), from that run's report
  (#98, R4), and read against the grill of #78 that ADR-0021 came from, questions Q1, Q2, Q8 and Q16.

## Context

ADR-0021 decision 1 says nothing irreversible happens on a pre-accepted answer alone: "no commit, no
body rewrite, no ticket filed, no merge". Its title says a scripted session commits only to
confirmed answers, and the glossary said nothing is committed on one. The grill it came from aimed
those words at the morning workflow: its first draft's Apply produced an integrator commit, a body
rewrite, a new ticket or an ADR candidate (#78, Q1 and Q8), and its Hand-over merged behind
`land: true` (Q2). The same record kept the night's lanes going. The glossary's *Standing answer* is
"a launch-time instruction that a night's lanes and grills take each recommendation as decided
instead of stopping for the human", and the overnight workflow's implement prompt, edited under
#78's Q16, tells a lane to take its weighed recommendation "and keep going".

So the text forbade what the record meant to allow, and nothing marked the case it meant to bar. On
the night of 2026-10-18 the lanes committed code on pre-accepted answers, as intended, and five
times also changed a document a session steers by on no confirmed answer. #67's lane committed its
pre-accepted decision D1 to `docs/agents/landing-work.md`. #68's and #69's lanes added glossary
entries their tickets did not ask for, citing nothing. Two reviewers made steering changes of their
own: #68's added `--both-halves` with a line in `AGENTS.md`, and #65's rewrote the Partial rule in
`docs/agents/triage-labels.md`. The night's reviews held none of them: #67's reviewer let the D1
sentence stand provided the landing waited for a confirmation, the report found #68's and #69's
entries, and the review of the landing pull request found the reviewers' own two.

## Decision

1. **ADR-0021 decision 1 governs what a session applies, not the code a lane builds.** A lane may
   commit code to its own branch on a pre-accepted answer. The human's landing (ADR-0021 decision 5)
   accepts or reverses it, and the night's report lists each human-owned decision a landing would
   carry. Nothing else rests on a pre-accepted answer alone: no change to a steering document, no
   body rewrite, no ticket filed, no merge.
2. **The steering documents are** `GLOSSARY.md`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`,
   `CODING_STANDARDS.md`, `docs/agents/`, `docs/adr/` and `contracts/`. Adding a change request is not a change to one.
3. **A change to a steering document cites its authority in the commit:** an acceptance criterion of
   the ticket it serves, where the human wrote or approved that ticket, or a confirmed answer. A
   pre-accepted answer, a follow-up a night raised, an archived ticket's proposal and a reviewer's
   finding are not authority. The steering check holds a commit that cites none; the reviewer judges whether what it
   cites decides the change.
4. **A lane that needs a new term or guide sentence proposes it** in the issue's `## Answer` comment,
   under `## Proposed steering changes`. The report puts each one to the human, and once it is
   confirmed a session commits it on its own branch (ADR-0021 decision 4).

## Alternatives rejected

- **Make the lanes match the text.** Under a standing answer a lane would record its recommendation
  and stop, so the grant would speed up grills only. Rejected: the throughput is what the grant is
  for (ADR-0021, Consequences), and code already waits for the human's landing.
- **Leave the wording and police steering commits only.** Cheapest. Rejected because the next reader
  of ADR-0021 still could not tell whether its text or the implement prompt wins, and that ambiguity
  is how the D1 sentence was committed and then let stand.
- **A reviewer standard instead of a check.** Rejected: the reviews of 2026-10-18 held none of the
  five changes. Whether a commit cites an authority is mechanical; whether the authority decides the
  change is the reviewer's call.
- **Check only the overnight workflow's branches.** Rejected: a lane that slips past the workflow's
  checks still reaches a pull request, and the human's own commits gain the same trace.

## Consequences

- ADR-0021's status line records the amendment; its six decisions otherwise stand.
- `GLOSSARY.md` gains *Steering document*, and *Pre-accepted answer* says what may rest on one.
- `.github/steering.py` gains the authority rule, for commits made after the rule lands. The
  overnight workflow's implement and review prompts carry decisions 3 and 4.
- All five were kept: Adam confirmed them on #98 before `overnight/2026-10-18` landed
  (https://github.com/adammedford/spa-platform/issues/98#issuecomment-6253500774 for R4 and D1,
  https://github.com/adammedford/spa-platform/issues/98#issuecomment-6254039291 for the reviewers'
  two).

## Evidence

- On `overnight/2026-10-18`, with no authority: 7569b1f (#68, *Local check*, citing nothing),
  b5aa97d (#69, *Terraform floor*, citing nothing), #67's D1 sentence in
  `docs/agents/landing-work.md` (in 469ea61, on D1's pre-accepted answer), 2b89124 (#68's review,
  `--both-halves` and its `AGENTS.md` line) and d606340 (#65's review, the Partial rule).
- With one: 23eeb24's *Test companion*, *Harness* and *Landed*, "worded as archived overnight ticket
  69 proposed and #66 accepted", which #66's acceptance criteria ask for and Q22–Q42, confirmed on
  #66, decided (https://github.com/adammedford/spa-platform/issues/66#issuecomment-6242517303); and
  469ea61's glossary entries, "confirmed by Adam on #67".
- #78's grill: https://github.com/adammedford/spa-platform/issues/78#issuecomment-6242544342 (Q1,
  Q2, Q8, Q16), and its first draft:
  https://github.com/adammedford/spa-platform/issues/78#issuecomment-6242544158.
- #67's review of D1: https://github.com/adammedford/spa-platform/issues/67#issuecomment-6246431768.
