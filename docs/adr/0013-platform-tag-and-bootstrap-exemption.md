# ADR-0013: Name the platform tag `Platform = spa-platform`, and exempt the bootstrap root

- **Status:** Accepted. **Amended 2026-10-23 by
  [ADR-0029](0029-prove-teardown-through-every-reader-of-the-platform-tag.md): teardown is proved
  through every reader of the platform tag, not the tagging query alone; the decision stands.**
- **Date:** 2026-10-12
- **Deciders:** integrator
- **Plan ref:** No decision row. The plan names "the platform tag" in three places without defining
  it: `docs/plan/05-infrastructure-and-cost.md` → Teardown, `docs/plan/04-tests.md` → P-05, and
  `docs/plan/03-workstreams-and-tasks.md` → task 1.6a. Raised as change request
  `contracts/changes/1.6a-platform-tag-convention.md` by workstream 1.

## Context

Teardown proves the account is empty by querying one tag. Three parties have to agree on the same
string or the proof is vacuous: workstream 1 applies the tag in every Terraform root and in
`certs.sh`, workstream 1 queries it at the end of `teardown.sh`, and workstream 6 asserts the query
returns nothing (P-05). A tag applied under one key and queried under another would pass P-05 while
leaving the account full of billable resources.

Tasks 1.0 and 1.6a had to tag something to be written at all, so they tagged as below and filed the
change request asking for confirmation before task 1.7 and the workstream 6 tests are written
against it.

Two resources complicate it. The Terraform state bucket and the read-only CI plan role are created
once by `infra/bootstrap` and are meant to **survive** teardown — the plan destroys the three roots
in reverse order and leaves the state bucket "last, by hand".

## Decision

`Platform = spa-platform` is the platform tag, alongside `Cell` and `ManagedBy` on every resource in
the three roots and on the imported wildcard certificate.

Resources in `infra/bootstrap` carry `Platform = spa-platform-bootstrap` instead, plus `Cell`,
`Component = bootstrap` and `Root`, and so do not answer the platform query.

The query is an **exact value match**. No prefix match, no wildcard, no `contains`.

The normative table lives in [`contracts/resource-tags.md`](../../contracts/resource-tags.md), so
workstream 6 reads the string rather than inferring it from Terraform.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| One `Platform` value for everything, and a `teardown.sh` query that excludes the two bootstrap resources by ARN | Puts the exemption inside the script whose job is to prove the account is empty. The proof then depends on a hand-maintained exclusion list, which is the wrong place for it. Workstream 1 proposed and rejected this; the integrator agrees. |
| Record the convention in `contracts/infra-outputs.schema.json`, as the change request suggested | `outputs.json` is written *after* an apply. `certs.sh` needs the tag before any root has run, and teardown needs it when outputs may be stale or already gone. Reading a teardown constant out of an apply artefact is circular. |
| Record it in `docs/plan/02-contracts.md` | `docs/plan/` is a frozen export and is never edited in place. A decision taken after the 11 October 2026 export belongs in an ADR. |
| A distinct tag key for bootstrap, e.g. `PlatformBootstrap = spa-platform`, removing the shared prefix | Marginally safer against a wildcard query, but it would rewrite tagging in two landed tasks to defend against a query form this ADR forbids outright. |

## Consequences

- Workstream 6 can write P-05 now, against a string it reads from a contract.
- The shared prefix between the two values is a live hazard, and the exact-match rule is the whole
  defence. A wildcard query would sweep up the state bucket and the CI role, and teardown would then
  fail on every run.
- The bootstrap resources are removed by hand. Nothing automated deletes them, which is the point.
- Untaggable resources — Cognito app clients, resource servers and managed-login branding styles —
  are invisible to the query. P-05 holds only because each is a child of the tagged user pool, whose
  destruction removes it. `contracts/resource-tags.md` states that as a rule, and a root that breaks
  it files a change request.
- A change of value before the first apply is a one-line edit per root plus `PLATFORM_TAG_VALUE` in
  `certs.sh`. After the first apply it is a change request.

## Evidence

Tasks 1.0 and 1.6a already tag as decided: `infra/bootstrap/main.tf` (`local.tags`, with the
`spa-platform-bootstrap` value and a comment naming P-05) and `tools/scripts/certs.sh`
(`PLATFORM_TAG_KEY`, `PLATFORM_TAG_VALUE`, and the tag-on-import step). Both were written and
statically validated on 12 October 2026 and have not been applied. P-05 is the standing check.
