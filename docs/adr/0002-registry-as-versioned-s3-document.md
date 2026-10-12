# ADR-0002: Keep the registry as one versioned JSON document in S3

- **Status:** Accepted
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D2 (Doc revision 36)

## Context

An app exists only as a registry entry, a release prefix in S3 and an OAuth client. The gateway
needs the whole routing, OAuth and policy configuration for every app, refreshed at runtime, with
no restart on change and no new runtime resource per app.

## Decision

One JSON object at `registry/registry.json`, polled with a conditional GET, validated, then swapped
whole. The gateway accepts or rejects a document entire: there is no partial application. Each entry
carries a fingerprint computed from its canonical JSON, and an app's cached options are evicted when
its fingerprint changes. `revision` must exceed the live one; a rollback is the old content under a
new revision.

Validation lives in one library that both the gateway and `spactl` call, so they cannot disagree.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| DynamoDB table | A new runtime resource, and atomicity across entries would have to be built. |
| AWS AppConfig | A new runtime resource for a document S3 already serves. |
| One object per app | Not atomic. A reader could observe a half-applied change set. |

## Consequences

- Atomic by construction, and no new resource. S3 versioning gives history and rollback for free.
- A change is live within one poll interval (15 seconds, jittered by up to 20%).
- A bad document is rejected whole and the last known good snapshot keeps serving, so the registry survives a publisher's mistake and an S3 outage.
- At a cold start there is no snapshot to keep, so apps whose secrets cannot be read are left out and reported, and the rest go live.
- The gateway cannot write the registry; the task role forbids it (checked by P-03).
- Phase 1 has no review or audit on registry writes. Phase 2 adds both.

## Evidence

Stand-in run S-D1 to S-D3, S-D7, S-D8; real-handler run R-C1, R-C2, R-C5. Test N-14. Schema and
validation rules in `docs/plan/02-contracts.md` → Registry document.
