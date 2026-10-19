# Phase 2 issues: the plan

Drafted 2026-10-19 by the planning session that found the C# lanes idle (#129's lanes for 6.3,
6.5 and 2.1b made no commit). It rests on the ADR draft beside this file and on the change
requests under `contracts/changes/` that the same session drafted. Nothing here is filed, decided
or committed on its own authority: the human files the spec issue, lands the ADR and decides each
request (ADR-0021 decisions 1 and 6, ADR-0023 decisions 3 and 4).

## What was drafted, and where each piece lands

| Draft | Where it is now | Where it lands | How |
| --- | --- | --- | --- |
| ADR draft: a Phase 2 package's hermetic half opens at G2 | `.scratch/phase-2/adr-draft-phase-2-hermetic-work-opens-at-g2.md` | `docs/adr/00NN-phase-2-hermetic-work-opens-at-g2.md` and a row in `docs/adr/README.md` | The session that commits it takes the next free number (`docs/adr/README.md`); the commit carries `Authority: #<spec>` |
| Change requests `p2-*.md` and `mr-*.md` | `contracts/changes/` on this branch, each `Status: open` | The same files, with a `## Decision` section and the status line changed | The integrator decides and edits; editing a change request is a steering change, so that commit also carries the trailer |
| This plan and the spec and ticket bodies | `.scratch/phase-2/` | GitHub Issues | `gh issue create`, per `docs/agents/issue-tracker.md` |

## The landing sequence

Each step needs the one before it, because every steering change cites the spec issue and every
ticket cites a decided request.

1. **File the spec issue.** `gh issue create --title "<title>" --body-file
   .scratch/phase-2/spec-g6-continuity.md`. Its number is the authority for everything below, so it
   goes first. The PingFederate spec is a second issue, filed the same way from
   `spec-p2-6-pingfederate.md`.
2. **Land the ADR.** Move the draft to `docs/adr/00NN-...md` with the next free number, change its
   status line from draft to Accepted, add its row to `docs/adr/README.md`, and commit with
   `Authority: #<spec>` in the last paragraph. `.github/steering.py` holds the push otherwise.
3. **Decide the change requests**, in the order the tickets need them: `p2-2` and `p2-1` first,
   then `p2-3` and `p2-5`, then `p2-6`; the `mr-*` requests whenever. Each decision is a `## Decision`
   section and a status line, as `4.2-access-token-result-provider-unavailable.md` records one, plus
   the integrator edits it asks for (a pin in `Directory.Packages.props`, a row in
   `docs/dependencies.md`, a key in a schema, a type in `src/Platform.Abstractions`, a double in
   `src/Platform.Testing`). A ticket whose code needs a pin or a type is blocked until its request is
   decided (ADR draft, decision 5).
4. **File the tickets** as sub-issues of their spec (`gh issue create --parent <spec>`), each with
   the body drafted under "The tickets" below, with `--add-blocked-by` for every edge in its row, and
   `--label ready-for-agent` on the hermetic ones. A deployed ticket is blocked by #63 and gets no
   label until its blockers close; a ticket that needs a sitting gets `ready-for-human`.
5. **Let the next overnight run take them.** The collect reads open `ready-for-agent` issues with no
   assignee and no open blocker, and sends each to the lane its `**Owner:**` line names. A ticket
   that cannot start until its change request is decided is held by the blocked-by edge, not by a
   label.

## How the tickets are shaped

The body is the spec and the comments are the record (`docs/agents/issue-tracker.md`). Each ticket
body carries, in this order: `**Owner:**` (one of `ws1-infra`, `ws2-hosting`, `ws3-identity`,
`ws4-proxy`, `ws5-fixtures`, `ws6-verification`, `integrator`), `**Plan row:**` (`P2-1` … or
`none`), `## Parent` naming the spec, `## What to build`, and `## Acceptance criteria` as a checkbox
list where every box is a behaviour a test can fail. A ticket touches one owner's paths only
(`OWNERSHIP.md`); what it needs from an integrator path is a change request it names as a blocker.
Merge subjects are `Merge part of task P2-n: ...` for a hermetic half and `Merge task P2-n: ...` only
for the merge that carries the deployed proof (ADR draft, decision 3). What needs AWS goes in the
merge body's `Outstanding:` line, as every Terraform row of Phase 1 did.

<!-- FILL: the spec issues -->

<!-- FILL: the tickets, per package -->

<!-- FILL: the change requests and what each blocks -->

<!-- FILL: open decisions for the integrator -->

## What stays blocked, and why

The deployed halves wait on the same chain as Phase 1's gates: #125 (a manual code-flow login
re-proving 1.8) blocks #55 (1.3), which blocks #56 (1.4), which blocks #57 (1.6b) and #58 (1.7),
which block #63 (6.9, the runs that close G3, G4 and G5), which blocks #62 (6.7). Each is a sitting
in AWS. Nothing in this plan shortens it, and nothing in this plan applies anything in AWS: P2-4 and
P2-8 are Terraform packages and open at G5 as the plan says, and the DynamoDB table, the KMS key and
the parameters P2-1 and P2-2 need on the stack are workstream 1's, outside P-01's allowlist, and wait
with them (ADR draft, decision 6).
