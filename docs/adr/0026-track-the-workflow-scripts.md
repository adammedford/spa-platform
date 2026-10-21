# ADR-0026: Track the workflow scripts, so a workflow change is a branch, a review and a landing

- **Status:** Accepted
- **Date:** 2026-10-21
- **Deciders:** integrator
- **Plan ref:** No decision row. #135, grilled Q1 to Q15 on its "## Grilling record" comment and
  confirmed on #185 (the Confirm of 2026-10-21, with its overrides of Q1 and Q3); parent #133,
  item D.

## Context

`.gitignore` ignored `.claude/*` and un-ignored only `settings.json`, so the overnight workflow
script, `.claude/workflows/overnight.js`, existed in one checkout with no review, no history and
no check. Every edit to it was a foreground edit to the human's main checkout: the fixes of
2026-10-19 (#133, item C) and the ready-first scheduler of 2026-10-20 were made that way, and the
six tickets whose work lives only in that file (#100, #103, #104, #106, #108, #109) had no landing
to close under #100's rule. #77 to #87, the workflow specs, could be built nowhere else.

Two things decided the shape of the fix. A negation cannot reach inside an ignored directory, so
the exception has to be written against the directory's contents. And the script is not a plain
script: the Workflow tool wraps its body in an async function, so the file begins with
`export const meta` and uses `return` at its top level; `node --check` refuses the one and a bare
function wrapper refuses the other (the grilling record, under node v26.8.1).

## Decision

1. **The workflow scripts are tracked, and nothing else in the folder.** `.gitignore` un-ignores
   `.claude/workflows/`, ignores its contents again, and un-ignores `*.js` there alone. The mock
   harness (`overnight_harness.mjs`) and its fixture stay ignored until #133's follow-up tracks the
   harness as the script's test companion. The first commit carries the live file's bytes at
   commit time, post-run edits included, with no older version reconstructed into history (Q1 as
   confirmed, Q2, Q3).
2. **A workflow script is integrator code.** No `OWNERSHIP.md` line: `.claude/workflows/` is the
   integrator's through the `*` rule, and workflow tickets join the integrator lane. It is not a
   steering document (ADR-0023 decision 2): its authority is its ticket and the human's review at
   the landing (Q9, Q10).
3. **Every edit lands before the launch it is for.** An edit to a workflow script, an urgent one
   before a launch included, goes branch, review, `tools/land.sh`, fast-forward. No launch runs a
   `scriptPath` from an unlanded copy (Q5).
4. **CI parses each workflow script.** The Documents job runs `.github/workflow-parse.py`, which
   rewrites `export const meta` to `const meta`, wraps the body in an async function it never
   calls, and asks node to parse the result, naming the file on a `SyntaxError`; its companion
   `.github/workflow_parse_tests.py` proves a good script passes and a broken one fails. The check
   uses the node the `ubuntu-latest` image ships and prints its version; the macOS job does not run
   it (Q6, Q7). `tools/check.sh` is unchanged here (Q8).

## Alternatives rejected

- **Keep the folder ignored and edit in the foreground.** No review and no history, and lanes
  cannot build workflow tickets.
- **Track all of `.claude/`.** `worktrees/` and `settings.local.json` are per-machine, and whether
  the project skills under `.claude/skills/` are tracked is a decision of its own (Q11).
- **Make the script a steering document.** Its rules are already held where they are stated: the
  ADRs and `docs/agents/` it quotes.
- **`node --check` on the file as it is.** It rejects the top-level `return`.
- **A function wrapper without the `export` rewrite.** It rejects line 1.

## Consequences

- Collect refuses a launch over an uncommitted script edit, since the main checkout must carry no
  tracked change.
- A fast-forward of the main checkout silently overwrites the ignored live copy, so the first
  landing compares the live file against `origin/main`'s blob with `cmp` before the fast-forward
  and refuses it when they differ (Q4).
- Workflow tickets join the integrator lane, which #133 item A found already runs alone; its
  one-worktree-per-ticket (#136) is the remedy.
- #137 drops the `**Session:** foreground` rule, the `FOREGROUND_ONLY` entries and the collect
  prompt's reason "a git-ignored workflow file no lane may write" (Q12).
- The landing closes #103, #104, #106 and #108 (Q13); #100 and #109 keep a box only a run's
  evidence can tick.
- The skills under `~/.agents/skills` and `.claude/skills/`, and the mock harness, stay outside
  review until #133's follow-up (Q11).

## Evidence

`.gitignore`, the `.claude` block. The collect step of `.claude/workflows/overnight.js` ("sits at
origin/main with no tracked change"). The node parse results and the fast-forward probe in #135's
grilling record.
