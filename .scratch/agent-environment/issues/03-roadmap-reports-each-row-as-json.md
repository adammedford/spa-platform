# 03: `tools/roadmap.py` reports each plan row's state as JSON

**Owner:** integrator
**Blocked by:** None (can start immediately).
**Status:** ready-for-agent

**What to build:** A program that needs to know how far the work has got asks the script that
already derives it. `tools/roadmap.py` reads the tags, the merge subjects and the plan's task tables
to write the roadmap's generated blocks; a JSON mode prints the same derivation for a program to
read: the gates closed, and each plan row's state, landed, part-landed or open.

The overnight run's collect step is the first consumer. Its prompt carries a rule of its own, that
a row is landed when a first-parent merge subject names it, and that rule misses 29 rows that landed
inside merges whose subjects name no id (#9, `fb4899a`, `a00b131`). The collect agents of 2026-10-15
and 2026-10-17 both noticed and read the roadmap's way instead. Read literally, the rule sends 29
finished rows back out for work.

- The JSON comes from the same functions as the generated blocks, so the two cannot disagree.
- The overnight collect step's landed rule becomes one line that runs the JSON mode. That workflow
  is a local file, ignored by git, so the session that lands this ticket makes the edit in Adam's
  checkout.

- [ ] The JSON mode's landed rows are exactly the rows the generated blocks count as landed, held
      by a case in the script's tests.
- [ ] The rows landed by #9, `fb4899a` and `a00b131` read as landed.
- [ ] The overnight collect step reads landed rows from the JSON mode.
