# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`GLOSSARY.md`** at the repo root: the platform's vocabulary, including which synonyms to avoid.
- **`docs/adr/`**: read the ADRs that touch the area you're about to work in. `docs/adr/README.md` indexes them and maps each to its origin in the plan.
- **`docs/plan/`**: the task-level detail. `AGENTS.md` says which file to read for which task, and `docs/plan/README.md` records what each file was exported from.

All three exist and are populated. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) extends them when terms or decisions get resolved.

## File structure

This repo is **single-context**: one glossary and one ADR directory at the root.

```
/
├── GLOSSARY.md                     ← vocabulary
├── docs/
│   ├── adr/                        ← live decision record, indexed by README.md
│   │   ├── README.md               ← index, and the plan/ADR split explained
│   │   └── 0001-<slug>.md
│   └── plan/                       ← frozen export, read-only
└── src/
```

## Two decision surfaces, one of them read-only

`docs/plan/` is an **export** of a Claude Doc, taken 11 October 2026. Its README names the Doc as the
source of truth, so a plan file is never edited in place: a disagreement means the folder needs a
fresh export, not a patch.

`docs/adr/` is the **live decision record**. ADR-0001 to ADR-0012 restate decisions D1 to D12 from
`docs/plan/01-plan.md`, each citing the plan row it came from.

So:

- **Reading a decision?** Either works. The ADR carries the alternatives and consequences in full; the plan's table is the one-line version.
- **Recording a new or revised decision?** Write an ADR. Never edit `docs/plan/`.
- **Writing anything else?** `docs/plan/` and `docs/adr/` are both integrator-owned. A workstream that needs a change there files `contracts/changes/<task-id>-<slug>.md` and stops.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `GLOSSARY.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0008 (session required before any asset), but worth reopening because…_

ADR-0017 records how the G0 report changes ADR-0001, ADR-0005 and ADR-0011. Check an ADR's
**Status** line and the annotated gate tags reachable from origin/main before treating a gate as closed.
