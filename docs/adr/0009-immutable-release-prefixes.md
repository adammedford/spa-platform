# ADR-0009: Publish releases as immutable prefixes named by the registry

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D9 (Doc revision 36)

## Context

Phase 1 acceptance criterion 5 requires release routing and a rollback with no deployment. A
mutable "current" prefix would make a release switch a copy operation, with a window where the
prefix is half-written.

## Decision

A release is an immutable prefix, `apps/{appId}/releases/{release}/`, and the registry names the
live one. `spactl` writes every object with `If-None-Match: *` in single-part uploads. The bucket
policy requires that header under `apps/` and denies deletes there to everyone but the teardown
role.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| A mutable "current" prefix | A switch becomes a copy with a half-written window, and history is lost. |

## Consequences

- Release and rollback are registry changes, live within one poll interval.
- A published release cannot be altered. This is enforced by the bucket policy, not by gateway code, and checked by P-04. Without the delete rule, a delete followed by a write would replace a release.
- Cache policy follows the path: `index.html` and non-asset files are `no-cache` with an ETag; files under `assets/` get one year and `immutable`.
- Content type comes from the file extension, not from what S3 stored, and every response carries `nosniff`. An unknown extension is served as `application/octet-stream`.
- `spactl` checks that the prefix exists; the gateway does not.
- S3 is a hard dependency for assets in Phase 1 (T16). Phase 2 adds caching.

## Evidence

Test P-04, criterion 5 (F-06). Layout in `docs/plan/02-contracts.md` → S3 layout.
