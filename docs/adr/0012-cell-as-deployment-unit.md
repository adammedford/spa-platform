# ADR-0012: Make the cell the deployment unit

- **Status:** Accepted. **Amended 2026-10-20 by
  [ADR-0025](0025-one-key-ring-per-cell-registered-by-hosting.md): the data-protection application
  name is `spa-platform-{cell}`, and the cell's key ring lives in Parameter Store, registered by
  hosting.**
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D12 (Doc revision 36)

## Context

Each SPA gets a logical boundary: its host, OAuth client, scheme names, encryption purposes, session
records, S3 prefix, API list and content security policy. But the process is shared, and so are its
memory, task role, data-protection key ring and registry. Code execution inside the gateway reaches
every session and every client secret in that deployment (T14). Logical isolation does not change
that.

## Decision

The deployment unit is a cell — the whole stack: ALB, service, bucket, registry, secrets prefix and
session store. Every module takes a cell name from day one. `Platform:Cell` is part of every resource
name and of the data-protection application name.

Phase 1 runs one cell. Phase 2 assigns apps to cells by data classification.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Assuming one deployment forever | Retrofitting a cell name through every module and resource name later is the expensive kind of change. |

## Consequences

- Phase 2 isolates risk classes by instantiating the same stack again, rather than by adding isolation inside the process.
- "No per-SPA runtime resources" still holds inside each cell.
- The task role is scoped to one cell's registry, releases and secrets, and cannot write the registry (P-03).
- Phase 1's answer to a compromised gateway process is least-privilege IAM only. Cells are the Phase 2 answer.

## Evidence

Isolation model and threat T14 in `docs/plan/01-plan.md`. Test P-03.
