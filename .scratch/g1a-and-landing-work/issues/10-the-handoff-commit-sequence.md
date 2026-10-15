# The handoff commit sequence, and what skeleton-v1 targets

Type: grilling
Status: closed
Blocked by: 09, 11

## Question

The map's destination is now five artifacts plus a set of corrections, and three mechanical traps
constrain the order they land in. This ticket decides the sequence, which is the last thing between
the map and handoff.

**The traps.**

1. `.github/steering.py:31` holds `"docs/gates/"` in `PLANNED` and **fails when a planned path
   appears**. So `docs/gates/G1a.md`, `docs/gates/README.md` and the removal of that entry are **one
   commit**, or CI and `pre-push` are both red.
2. `docs/agents/landing-work.md` is **not** in `PLANNED`, so it and the `AGENTS.md` line pointing at
   it are **one commit**, or the steering check fails on a dangling pointer.
3. Ticket 05's ordering rule: **`skeleton-v1` targets a commit that already contains
   `docs/gates/G1a.md`**, so the tag and its own attestation are one tree.

**What has to land.** `landing-work.md` + its `AGENTS.md` pointer; `docs/gates/README.md` +
`G1a.md` + the `PLANNED` edit; ADR-0014 + its `docs/adr/README.md` row; the `CODEOWNERS` header
rewrite; the `.github/CODEOWNERS` guard in `pre-push` and CI; `.githooks/pre-push:9`'s false
"per worktree" header; `ci.yml`'s "Four jobs" header (there are three) and its Tests-step comment
claiming it runs A-01 to A-04 (they run nowhere); `README.md`'s five stale claims; and the
`GLOSSARY.md` entries ticket 03, 04 and 05 each asked for (handle vs team, "lanes", session, claim,
gate checklist, attest).

**And one unmerged commit.** `origin/main` is `33eb5fb`; **`357a475` is not merged**. It carries
`FreezeMarkersTests.cs`, `steering.py` and the `pre-push` changes, and is green at **308** tests
(<https://github.com/adammedford/spa-platform/actions/runs/38425898285>). The freeze markers are the
only mechanical evidence of *which parts are draft versus frozen* — the point of the word
"non-identity" in G1a's Proves column, and so arguably part of item 2's evidence.

What has to be settled:

1. **How many commits, and which groupings?** The traps force three bundles; the rest is judgement.
2. **Does `357a475` merge before the tag?** If item 2's evidence leans on the freeze markers, yes,
   and G1a's run URL must then be a run on `main` *after* that merge — not `38424905236`, which
   ticket 05's items currently cite.
3. **Which commit does `skeleton-v1` point at**, and is it a merge or a direct commit on `main`?
4. **Does the integrator's own handoff work get a PR?** The map's Review row carves out the
   integrator's skeleton work as merging on CI plus the human, which it calls the weakest link in the
   model. This is that case, at the moment the gate closes on it.

## Answer

<!-- the commit sequence, the tag's target, and whether 357a475 lands first -->

## Closed: out of scope (the cut)

`Status: resolved` is wrong for this and it is not resolved; it is **closed unresolved**, because
the thing it decides about no longer exists. Commit 3d344fd cut the coordination apparatus down to
what one author needs: the repository had 6,437 lines of process against 611 lines of product code,
and none of the platform worked yet.

What that removed, which this ticket depended on:
- Six artifacts became two: `OWNERSHIP.md` and `docs/agents/landing-work.md`, both landed in
  3d344fd with the script and CI changes, on the branch that was already open.
- The three mechanical traps that forced artifacts into shared commits are gone with the artifacts.
  The one that remained — a steering pointer must land with its target — was satisfied by putting
  `landing-work.md` and the `AGENTS.md` line in the same commit.
- `skeleton-v1` targets the merge of that branch into `main`. Its exit test, "the solution builds,
  the gateway starts on stubs and answers 404 to an unknown host", is proved by the CI run on that
  merge, which the tag message names.
