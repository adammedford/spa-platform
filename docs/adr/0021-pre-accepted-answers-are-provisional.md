# ADR-0021: A pre-accepted answer is provisional; a scripted session commits only to confirmed answers, and the human lands

- **Status:** Accepted
- **Date:** 2026-10-17
- **Deciders:** integrator
- **Plan ref:** No decision row. The grill of #78 (the morning workflow spec), questions Q1, Q2, Q7
  and Q8; the standing answer of 2026-10-15 that the overnight runs of 15, 16 and 17 October ran
  on; #67, which decides what enforces review before a merge and is not touched here.

## Context

On 2026-10-15 the human gave the overnight run a standing answer: a lane facing a human-owned
decision takes its own weighed recommendation and keeps going, and a grill run on paper writes
`Answer: yes, pre-accepted by Adam, 2026-10-15` under every question. The night's throughput
depended on it. The morning's report then asked the human to "confirm or override" the answers.

By 2026-10-17 the word had drifted. The run of that night left 52 pre-accepted answers that no
record shows anyone confirming; a review of PR #76 the same day called them "accepted" and required
issue bodies to be rewritten to them, and #66's body was. The morning workflow spec, as first
written, would have committed glossary and guide edits on a pre-accepted answer and merged the
night's pull request behind a `--land` flag. Every agent session and the human act through one
GitHub account, so nothing in the tracker distinguishes what the human decided from what a session
decided on the human's behalf.

## Decision

1. **A pre-accepted answer is provisional.** It records the recommendation a night took under a
   standing answer. Nothing irreversible happens on it alone: no commit, no body rewrite, no ticket
   filed, no merge.
2. **Confirmation is one comment by the human on the run's `Overnight <date>` issue**, beginning
   `Confirm`, naming the questions it covers, with overrides as `Q<n>: <answer>` lines. It is
   self-attested, like the review record #67 proposes: the guard is against haste, not forgery, and
   no agent session writes one. Without it, a session that would apply answers reports what it
   would do and does nothing.
3. **The standing answer is granted per launch, default off**, and the night's report states whether
   it was granted.
4. **On a confirmed answer, a scripted session may commit to the documents a session steers by** —
   the glossary, the agent guides, a change request's status line — on its own branch, for review
   as any pull request. It drafts, and never commits, a frozen contract's sentence or an ADR.
5. **The human lands.** No workflow merges into `main`; the morning hands over the landing command
   and the human runs the landing script. The cleanup that follows a landing is a separate, explicit
   invocation that refuses until `main` contains what it cleans up after.
6. **A ticket's body is rewritten only to carry confirmed decisions**, naming the questions applied
   and preserving the previous body in a comment; a spec's body only by the session that grilled it;
   the overnight grill comments and never rewrites.

## Alternatives rejected

- **Pre-accepted equals accepted.** Cheapest for the night and the morning; it is what PR #76's
  review assumed. Rejected because it makes a decision nobody read indistinguishable from one the
  human took, and the only evidence of the difference — this ADR's context — had to be reconstructed.
- **`--land` behind a review record and green CI.** Safe once #67's landing-script check exists,
  and it saves one typed command. Rejected because the merge is the one act here that cannot be
  undone by a later commit, and #67's accepted answer already makes the human's reading the decision
  to land; a flag that merges would bring the exception back through the side door.
- **A second GitHub account for the human.** Would make confirmation verifiable. Rejected for the
  same reason #67's grilling rejected branch protection: on a repository with one person, a second
  identity is a bypass with extra steps.

## Consequences

- The morning workflow (#78) has no `land` argument, a dry Apply until a `Confirm` comment exists,
  and an `after` mode for cleanup. The overnight workflow takes `standingAnswer` as a launch
  argument and forbids its agents from writing a `Confirm` comment.
- `GLOSSARY.md` gains *Standing answer*, *Pre-accepted answer*, *Confirmed answer*, *Review record*,
  *Hand-over* and *Check*; `docs/agents/issue-tracker.md` carries the body-rewrite rule.
- The 52 answers of 2026-10-17 were confirmed by comment on #67, #66 and #65 the day this was
  decided. Those confirmations left Q20 and Q21 of that night open.
- Nights run without a standing answer stop more often. That is the price of knowing which
  decisions were the human's; a launch that wants the throughput grants the answer and says so.
