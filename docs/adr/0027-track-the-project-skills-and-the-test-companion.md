# ADR-0027: Track the project skills and the overnight script's test companion under the workflow scripts' rule

- **Status:** Accepted. **Amended 2026-10-23 by ADR-0030:** decision 2's CI step runs only when a
  file under `.claude/workflows/` changes, and runs every workflow script's test companion, not
  the overnight script's alone; `tools/check.sh --full` runs the same companions under the same
  condition, so the consequence that it does not run the companion no longer holds. The other
  decisions are unchanged.
- **Date:** 2026-10-21
- **Deciders:** integrator
- **Plan ref:** No decision row. #213, grilled Q1 to Q19 on its "## Grilling record" comment, which
  are #226's Q26 to Q44, confirmed on #226 (the Confirm of 2026-10-21, with its overrides of Q32,
  Q37 and Q43); raised by #135 Q11; parent #133. **Amends** ADR-0026 decision 1, whose status line
  records it.

## Context

ADR-0026 tracked the workflow scripts and left two things ignored on purpose: the overnight
script's mock test file and its fixture, the script's only behavioural proof, and `.claude/skills/`.
The project skill `wiz` walks the sittings that apply and destroy the cell. Its tracked draft
(`.scratch/sitting/wizard-SKILL.md`) and its live copy had drifted 40 lines apart, and three
defects found in sitting C (#135's comment of 2026-10-21) had no reviewable home.

## Decision

1. **`.gitignore` also un-ignores the companion, its fixtures and the project skills.** The lines
   are `!.claude/workflows/overnight_tests.mjs`, `!.claude/workflows/fixtures/` and
   `!.claude/skills/`; anything else in the workflows folder stays ignored (Q26, Q36).
2. **The overnight script's tests are its test companion,** `overnight_tests.mjs`, which stays
   `.mjs`, with its fixture of 2026-10-20 tracked as it is. CI's Documents job runs it on every
   push, after the parse check, using the image's node (Q27 to Q30).
3. **A project skill is integrator code under ADR-0026 decisions 2 and 3:** not a steering
   document, and every edit lands before the sitting that runs it, so a defect found mid-sitting
   stops the sitting at that stage (Q37, Q38).
4. **The skill keeps the name `wiz`;** the `.scratch` draft is removed and its citations repointed
   (Q39).
5. **User-level skills under `~/.agents/skills` stay outside the repository and its review,** and
   nothing is vendored (Q33).

## Alternatives rejected

- **Keep the skills ignored.** No review and no history, and the copies drift.
- **Make a skill a steering document.** Each field fact would wait on a confirmed answer, and its
  rules are already held where they are stated.
- **Allow a mid-sitting skill fix before it lands.** Unreviewed, and it leaves the main checkout
  dirty.
- **Name the tests "the harness".** The glossary reserves the word.
- **A deterministic clock before landing.** It rewrites the only proof before it has failed.
- **Gate the companion's CI step on paths.** The Documents job runs everything.

## Consequences

- The first landing compares the live companion, fixture and `SKILL.md` against the branch's first
  commit before the fast-forward, then removes the stale `overnight_harness.mjs` (Q42).
- A new agent kind or return shape in the script fails the companion until it is updated in the
  same change.
- Follow-ups: the redaction script and its companion (W-4, Q41), and moving the skill's cached
  ticket expectations onto the tickets (Q40).
- `tools/check.sh` does not run the companion; CI on the landing does (Q32).

## Evidence

The companion's three passing runs in #213's grilling record (1.2 s, node v26.8.1), and twenty more
on node v22.22.0 at the landing (1.3 s each). The `git check-ignore` probe in that record. #135's
fast-forward probe. #135's comment of 2026-10-21T02:36Z.
