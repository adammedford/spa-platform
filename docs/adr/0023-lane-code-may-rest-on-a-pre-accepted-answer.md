# ADR-0023: A lane's code may rest on a pre-accepted answer; a steering document changes only on a cited authority

- **Status:** Accepted
- **Date:** 2026-10-18
- **Deciders:** integrator
- **Plan ref:** No decision row. Amends ADR-0021 decision 1. The retrospective of the overnight run
  of 2026-10-18 (#98, R4), and the grill of #78 that ADR-0021 came from, questions Q1, Q8 and Q16.

## Context

ADR-0021 decision 1 says nothing irreversible happens on a pre-accepted answer alone: "no commit, no
body rewrite, no ticket filed, no merge". Its title says a scripted session commits only to
confirmed answers, and the glossary said nothing is committed on one. The grill it came from aimed
those words at the morning's Apply, whose first draft produced an integrator commit, a body rewrite,
a new ticket or an ADR candidate, and a merge (#78, Q1 and Q8). The same record kept the night's
lanes going: a standing answer exists so that "a night's lanes and grills take each recommendation
as decided instead of stopping for the human", and the overnight workflow's implement prompt tells a
lane to take its weighed recommendation "and keep going" (#78, Q16).

So the text forbade what the record meant to allow, and nothing marked the case it meant to bar. On
the night of 2026-10-18 the lanes committed code on pre-accepted answers, as intended, and also
changed documents a session steers by. #67's lane committed its pre-accepted decision D1 to
`docs/agents/landing-work.md`. #68's and #69's lanes added glossary entries their tickets did not ask
for, citing nothing, and #66's cited an archived ticket's proposal. No review held any of them:
#67's reviewer let the D1 sentence stand provided the landing waited for a confirmation, the report
found #68's and #69's entries, and the retrospective found #66's.

## Decision

1. **ADR-0021 decision 1 governs what a session applies, not the code a lane builds.** A lane may
   commit code to its own branch on a pre-accepted answer. The human's landing (ADR-0021 decision 5)
   accepts or reverses it, and the night's report lists each human-owned decision a landing would
   carry. Nothing else rests on a pre-accepted answer alone: no change to a steering document, no
   body rewrite, no ticket filed, no merge.
2. **The steering documents are** `GLOSSARY.md`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`,
   `docs/agents/`, `docs/adr/` and `contracts/`. Adding a change request is not a change to one.
3. **A change to a steering document cites its authority in the commit:** an acceptance criterion of
   the ticket it serves, where the human wrote or approved that ticket, or a confirmed answer. A
   follow-up a night raised, an archived ticket's proposal and a reviewer's finding are not
   authority. The steering check holds a commit that cites none; the reviewer judges whether what it
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
  four changes. Whether a commit cites an authority is mechanical; whether the authority decides the
  change is the reviewer's call.
- **Check only the overnight workflow's branches.** Rejected: a lane that slips past the workflow's
  checks still reaches a pull request, and the human's own commits gain the same trace.

## Consequences

- ADR-0021's status line records the amendment; its six decisions otherwise stand.
- `GLOSSARY.md` gains *Steering document*, and *Pre-accepted answer* says what may rest on one.
- `.github/steering.py` gains the authority rule, for commits made after the rule lands. The
  overnight workflow's implement and review prompts carry decisions 3 and 4.
- The D1 sentence and the three glossary entries without an authority were kept: Adam confirmed them
  on #98 before `overnight/2026-10-18` landed (R4,
  https://github.com/adammedford/spa-platform/issues/98#issuecomment-6253500774).

## Evidence

- On `overnight/2026-10-18`: 7569b1f (#68, *Local check*, no citation), b5aa97d (#69, *Terraform
  floor*, no citation), 23eeb24 (#66, citing "archived overnight ticket 69 proposed"), 469ea61 (#67,
  citing "confirmed by Adam on #67", the one change with an authority), and #67's D1 sentence in
  `docs/agents/landing-work.md`.
- #78's grill: https://github.com/adammedford/spa-platform/issues/78#issuecomment-6242544342 (Q1,
  Q8, Q16), and its first draft:
  https://github.com/adammedford/spa-platform/issues/78#issuecomment-6242544158.
- #67's review of D1: https://github.com/adammedford/spa-platform/issues/67#issuecomment-6246431768.
