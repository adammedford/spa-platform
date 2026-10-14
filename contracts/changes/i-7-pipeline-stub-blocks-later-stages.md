# Change request I-7: say that stage 4's stub blocks every later stage

- **Raised by:** workstream 4, API and security, during task 4.1
- **Tasks:** every task that fills a stage-10 entry point: 2.4, 3.9, 4.2 to 4.6
- **Date:** 2026-10-13
- **Status:** decided, 2026-10-13. See the Decision section below.

## What it cost

Task 4.1 filled in `MapPlatformProxy`, proved it over a socket at each end in its own test project,
then published the gateway to check the composed order and got an empty 404 from
`/api/orders/items`. The endpoint was registered and correct. Twelve tool calls, a throwaway probe
project and one confident wrong diagnosis later, the cause turned out to be
`UsePlatformHostResolution`: its stub sets 404 and returns without calling the rest of the pipeline,
so nothing from stage 5 onwards runs.

That is the right behaviour for the stub, and `src/Platform.Hosting` says so plainly: answering 404
to everything is correct with no registry, because every host is then an unknown host, and task 2.3
resolves the known ones.

The gap is that the consequence is written where the cause is, and the cost falls six stages later.
A session filling a stage-10 entry point reads `contracts/pipeline.md`, not workstream 2's stub.

Five tasks still to come fill a stage-10 entry point, and the same wall is in front of each of them.

## The change

One short section at the end of `contracts/pipeline.md`, after the three orderings: no stage after 4
runs in the composed gateway until task 2.3, so an entry point is proven in its owner's test project
and again in workstream 6's composed suites.

Nothing about the contract's substance changes. The pipeline order, the owners and the entry points
are untouched; this records a property of the skeleton that the contract's readers hit.

## Why this is a request rather than an edit

`contracts/pipeline.md` freezes at G1a and `skeleton-v1` is tagged, so `contracts/README.md` makes
any change a written request the integrator decides, never an edit. The rule carves out no exception
for a change that only adds prose, and a freeze with an exemption for harmless-looking edits is not
a freeze.

---

## Decision

Integrator, 2026-10-13. Accepted as written, and applied in the same commit as this request.

The section is prose about the skeleton. It changes no interface, no order and no owner, so no
workstream has to re-read anything it has already built against. Had it changed a stage, an owner or
an entry point's signature, it would have waited for a gate.

Recorded here rather than only in a commit message, because the first question anyone asks of a
frozen file is why it moved after the freeze.
