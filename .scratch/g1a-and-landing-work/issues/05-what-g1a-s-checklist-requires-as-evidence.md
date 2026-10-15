# What G1a's checklist requires as evidence

Type: grilling
Status: resolved
Blocked by: 01

## Question

`docs/gates/G1a.md` is the gate's exit test, item by item, each carrying the evidence that proves it
and closing with an attestation line. What are the items, and what counts as evidence for each?

The gate's exit test, from `docs/plan/01-plan.md`:

> Every shared file exists and compiles: the non-identity contracts, abstractions, test doubles, and
> the composition root with stub entry points. The solution builds. The gateway starts on stubs and
> answers 404 to an unknown host.

Plus task I-5's own: "green on the skeleton", and A-05 failing a change outside its owner's paths.

What has to be settled:

1. **The item list.** Is it the exit test's sentences, one per item, or the five tasks I-1 to I-5 with
   their "Done when" each? The two do not decompose the same way: I-2's schema tests are not named in
   the exit test, and the exit test's "answers 404 to an unknown host" is I-4's.
2. **What evidence is admissible.** A pasted command transcript, a CI run URL, a test count, a commit
   sha? The gateway's 404 was proved on this workstation over a raw socket, because the sandbox
   intercepts HTTP by Host header — so for that item a CI run cannot be the evidence, and the
   transcript has to be, which is worth stating rather than discovering later.
3. ~~Whether an item may be attested as convention-enforced.~~ **Moot.** Ticket 06 expected branch
   protection to be convention-carried; ticket 01 made the repository public, so all three checks are
   enforced on `main` and there is no convention-enforced item to word carefully. The question itself
   is still worth keeping in mind for a later gate, where a deployed item may have no automated
   evidence, but it does not apply to G1a.

   What ticket 01 did hand over is better evidence than expected: a **CI run URL** showing the
   workflow green on a runner, which is what task I-5's "done when" asks for.
4. **What the tag annotation says.** `git tag -a skeleton-v1` with what message, pointing at the
   checklist how — a path, a commit sha, both?
5. **Whether the same shape serves G0, G1b and the later gates.** Task I-7 wants a signed checklist
   for *each* gate, so this file is a template whether or not it is written as one. Decide if
   `docs/gates/` gets a stated convention now or is left to the next gate to generalise.

Needs ticket 01 resolved first: items 2 and 3 depend on what the CI run produced and what the account
plan permitted.

## Answer

**Six items, each carrying five evidence fields, plus a stated convention in `docs/gates/README.md`.**
Two of this ticket's own premises were wrong, and correcting them swapped which item is the hard one.

### Two corrections first

**The exit test was misquoted.** The blockquote above fuses two columns of `docs/plan/01-plan.md:232`.
The **Exit test** column is only *"The solution builds. The gateway starts on stubs and answers 404 to
an unknown host."* The *"Every shared file exists and compiles..."* sentence is the **Proves** column.
This matters because item 1's "the exit test's sentences, one per item" decomposes differently
depending on which columns you count.

**Item 2's central claim is false.** It says the 404 "was proved on this workstation over a raw
socket... so for that item a CI run cannot be the evidence". But `.github/workflows/ci.yml:62` is a
step named *"The gateway starts and answers 404 to an unknown host"*: it publishes the gateway, starts
it, and probes with `curl -H "Host: ..."`. Its own comment cites `AGENTS.md` to explain why a runner
differs from an agent sandbox. The step is on `main` and passed in run 38424905236, output
`nope.example / -> 404` and `app1.spa-poc.test / -> 404`. The ticket spotted the sandbox/runner
tension and resolved it the wrong way. The item CI structurally *cannot* evidence is **A-05's failing
path**, not the 404.

### The item list

The exit test's provable clauses, **not** the five tasks. The task shape imports what the gate does
not test (I-2's schema tests) and is task bookkeeping, which `AGENTS.md:76` forbids in a steering
file. A **"produced by"** column carries I-1 to I-5 instead, so provenance survives without any item
depending on a task's status staying true.

| # | Item | Source |
| --- | --- | --- |
| 1 | The solution builds, warnings as errors | Exit test, verbatim |
| 2 | The shared files exist and compile: the non-identity contracts, `Platform.Abstractions`, `Platform.Testing`, and `Program.cs` with every entry point a stub in its owner's project | Proves column — the only item that says what the skeleton *is* |
| 3 | The gateway starts on stubs | Exit test, verbatim |
| 4 | It answers 404 to an unknown host | Exit test, verbatim |
| 5 | CI is green on the skeleton, and A-05 fails a change outside its owner's paths | Task I-5 "Done when" |
| 6 | `docs/agents/landing-work.md` exists, and `AGENTS.md` points at it | **Decided by the human**, see below |

**Item 6 is a scope decision, made explicitly.** `docs/plan/01-plan.md:232`'s exit test is purely
about the skeleton building and starting; it says nothing about how work lands. The gate was therefore
either "the skeleton is frozen" (item 6 out) or "the six workstreams can start" (item 6 in). The
human chose **in**. The consequence is accepted and is now the gate's critical path: **G1a cannot
close until ticket 04 is resolved and its decisions are written into `landing-work.md`.** Ticket 04
resolved in the same sitting, so the dependency is satisfied in substance; what remains is writing
the file.

### What evidence is admissible

**Five fields per item**: the behaviour in one line; the CI run URL; the **head sha**; the exact
**job and step name**; and the **quoted output line that decides it**. A URL alone decays, because
GitHub does not retain workflow logs indefinitely — the link that proves the gate today reads as a
dead end to whoever audits G3. The quote survives log expiry, the sha makes the claim re-runnable,
and the step name is what stops "CI was green" standing in for an item no step actually checks.

That last one is a live hazard here, not a hypothetical: **four of fifteen test projects contain any
test source** (`Contracts.Tests`, `Platform.Testing.Tests`, `Platform.Abstractions.Tests`,
`fixtures/Fixtures.Tests`), all four `tests/Gateway.*` are **empty**, and `ci.yml`'s Tests-step
comment nonetheless claims the step runs "the hermetic gateway suites and the architecture tests A-01
to A-04". Those run **nowhere**. "306 tests green" is four projects' worth, three of them the
integrator's own. A checklist that trusted the comment would attest a vacuum.

Decisive quotes available today, from run **38424905236** on `main` at **`33eb5fb`**:
`Build succeeded. / 0 Warning(s)`; `total: 306, succeeded: 306`; `nope.example /healthz -> 200`,
`nope.example /readyz -> 503`, `nope.example / -> 404`, `app1.spa-poc.test / -> 404`.

### Item 4 cites CI, and says what the stub proves

Cite the CI step; drop the raw-socket transcript. Word the item to what it actually proves: *the
published gateway starts, serves `/healthz` before host resolution, and returns 404 at stage 4 for
every authority while the registry is empty*, with the probe lines quoted. Then one sentence
recording the honest limit: `src/Platform.Hosting/PlatformHostingExtensions.cs:54-59` returns 404
**unconditionally**, so with an empty registry every host is unknown and the step cannot distinguish
a *registered* host from an unknown one. Doing that needs a live snapshot, which stubs cannot have,
and **N-08** re-proves it at G4. True rather than flattering — and it removes the only manual step
from a gate that otherwise needs no workstation.

### Item 5 carries the one hand-run transcript, labelled as one

A green CI run **cannot contain a failing check**, so no run URL can evidence "a change outside its
owner's paths fails". PR #1 exercised only A-05's **allow** path, the integrator declaration.
`.github/path-ownership.py` has no self-test and nothing under `tests/` references A-05, so there is
no automated negative either. The alternative — holding G1a until A-05 has an automated test — needs
a home for that test, and every candidate is a solution-file change, which is scope this gate does
not have.

So: accept the hand-run transcript **for this item alone**, and label it. The evidence names it a
hand run, gives its date and the six scenarios covered, and the item closes with an explicit gap
line: *A-05 has no automated test; `docs/plan/01-plan.md:235` puts A-01 to A-05 in **G2's** exit
test, which is where it gets one.* This makes the gate's weakest evidence its most visibly weak,
rather than hiding it behind the same "CI green" phrasing as items 1 to 4.

### `docs/gates/` gets its convention stated now

A short `docs/gates/README.md` beside `G1a.md`. I-7 wants a signed checklist for each of seven gates,
so the shape is a template whether or not it is written as one — and `docs/plan/01-plan.md` cannot be
the generaliser, because its gates are not uniformly shaped: G1a is prose, G0 a cross-reference,
**G1b circular** ("The freeze is tagged" — a checklist derived from it would have one item attesting
the existence of the tag that points at it), G2 to G4 id lists, G5 ids plus "signed off". The map's
"Gate evidence" row has already settled every field, so writing it down costs a paragraph, and it is
the only copy that survives: `map.md` lives in `.scratch/` and is never committed.

The README also records two mechanical traps:

1. **The steering trap.** `.github/steering.py:31` lists `"docs/gates/"` under `PLANNED`, and the
   check **fails when a planned path appears** — by design, so an exemption cannot outlive the file.
   `docs/gates/G1a.md` and the removal of that entry must land in the **same commit**, or CI is red
   on arrival, in `pre-push` too. `AGENTS.md:80` and `:84` stop being forward references on that
   commit.
2. **The same trap applies to item 6.** `docs/agents/landing-work.md` is **not** in `PLANNED`, so the
   file and the `AGENTS.md` line pointing at it must also land together, or the steering check fails
   on a dangling pointer.

### The tag annotation

`git tag -a skeleton-v1` carrying the gate name and what it froze, the checklist path
`docs/gates/G1a.md`, and the CI run URL its items cite. **No sha in the body**: an annotated tag's
target *is* a commit, so a sha restates it. The question was what `git show skeleton-v1` gives a
reader with no network round-trip, and those three answer it.

Plus an ordering rule: **`skeleton-v1` targets a commit that already contains `docs/gates/G1a.md`**,
so the tag and its own attestation are one tree. A tag placed earlier points at a tree lacking its
own evidence. `git tag` is empty, so this is the repository's first tag and sets the pattern
`contracts-v1` follows at I-6.

### Test ids

G1a's only plan-attached id is **A-05**, on item 5. **N-08** appears on item 4 as a forward reference,
not a claim. **A-01 to A-04 must not appear**: `tests/Gateway.*` is empty so they run nowhere, and
`docs/plan/01-plan.md:235` assigns them to **G2**. `S-`, `R-` and `T-` are G0's and have no place
here. No `G1a-` prefix is invented.

### Who attests

A session may write `docs/gates/G1a.md` and fill every evidence block, but it leaves `Attested by:`
**absent or blank** for the integrator to complete. A session that pre-fills it, *even with the
human's name*, has attested, which the map's "Gate evidence" row forbids. Note also that
`required_signatures` is `false` in the live branch protection, so nothing mechanical backs reading
I-7's "signed" as GPG; the map's reading — attested by a named decider — stands.

### Corrections this ticket makes elsewhere

- **ADR number collision.** The map and ticket 03 both named the new CODEOWNERS ADR
  `docs/adr/0013-*.md`. **0013 is taken** by `0013-platform-tag-and-bootstrap-exemption.md`, which
  also has a row in `docs/adr/README.md`. It is **0014**, and that README needs the row. Both files
  corrected.
- **`README.md` is stale in five places, not one.** Beyond the `CODEOWNERS` row ticket 03 fixed: "It
  holds no product code yet", "`git init -b main` # the first commit is yours to make", "`dotnet
  test` has not been run yet", and "Git | Not initialised". They sit in a "State of the skeleton"
  table a G2 reader would take for gate state, so they land in the **same commit as the checklist**.
  This is handoff cleanup, not a gate item: the gate tests behaviours, not self-description. Removed
  from the map's "Not yet specified".
- **One stale line inside ticket 01's Answer**, worth not re-citing: it says "`main` had no CI run of
  its own". `main` now has run **38424905236** at `33eb5fb`, green on all three jobs — which is the
  run items 1, 3 and 4 should cite.
- **`ci.yml`'s own header says "Four jobs"; there are three.** Same handoff pass.
- **Vocabulary.** `GLOSSARY.md` has **Gate**, **Test ids** and **Evidence ids** (`S-`, `R-` and
  `T-` — a third prefix this ticket omitted) but no entry for a **gate checklist**, for
  **attest/attestation**, or for **evidence** as a gate artifact. The checklist introduces "attested
  by", so the term needs an entry, or the map's own vocabulary rule is broken by the map's own
  artifact.

## Addendum (ticket 08)

`Status: resolved` stands, and the ticket is not reopened — 09 depends on it and is already
unblocked. Precedent: ticket 06, reversed by ticket 01 via an addendum. One item's evidence changed.

**Item 5 cites a test, not the hand-run transcript.** This ticket accepted the six hand-run
scenarios as evidence *and recorded the gap*, pointing at `docs/plan/01-plan.md:235`, which puts
A-01 to A-05 in **G2's** exit test. Ticket 08 rejected the deferral for A-05: three of its decisions
rewrite `.github/path-ownership.py`, A-05 is the only mechanical guard on the six-workstream model,
and it has now been found wrong three times by review rather than by test — the `.github/CODEOWNERS`
shadowing (ticket 03), the truncated reason (357a475), and that same truncation **still live** in
91e2103, written after the fix. `tests/RepositoryChecks.Tests` lands before `skeleton-v1`.

**All five evidence fields stay.** Behaviour, run URL, head sha, job and step name, quoted decisive
line. A test-backed item fits them without loosening: the behaviour is "a change outside its owner's
paths fails", the job and step are `Build and test` / `Tests`, and the decisive line is the named
test result rather than a transcript paragraph. A shorter field set for test-backed items would make
`docs/gates/README.md` carry two conventions across seven gates, when most later items will be
test-backed.

**Item 5 names A-05 alone.** Re-measured: 4 of 16 test projects hold any tests, and all four
`tests/Gateway.*` are still empty, so **A-01 to A-04 run nowhere** and stay in G2's exit test.
`RepositoryChecks.Tests` becomes the fifth populated project. Without this clause the natural reading
of "item 5 now cites a test" is that the architecture rules are covered. This ticket's gap record
stands, minus its A-05 clause.
