# Resource tags

- **Freezes at:** G1a
- **Consumed by:** workstream 1, `teardown.sh` and `verify-teardown.sh`, and workstream 6's P-05
- **Source:** change request `changes/1.6a-platform-tag-convention.md`; ADR-0013, amended by
  ADR-0029 (change request `changes/issue-308-what-the-tag-query-proves.md`). Not
  `docs/plan/02-contracts.md`: the plan names the platform tag three times without defining it, so
  this file has no plan section behind it.

Every AWS resource this platform creates carries the same three tags. Teardown proves that nothing
carrying the platform tag remains in the platform project. It reads the tag through the tagging API
in the selected Region, and through IAM's own API, which the tagging API cannot read. So the value
workstream 1 applies and the value every reader matches have to be the same string. This file is
that string.

## The tags

| Tag | Value | Applied to |
| --- | --- | --- |
| `Platform` | `spa-platform` | Every resource in `infra/envs/poc/{idp,apps,platform}`, and the wildcard certificate `tools/scripts/certs.sh` imports to ACM |
| `Cell` | the cell name, e.g. `poc` | The same resources |
| `ManagedBy` | `terraform`, or `certs.sh` for the certificate | The same resources |

`Platform = spa-platform` is **the platform tag**: the one every reader of
`tools/scripts/verify-teardown.sh` matches exactly, and P-05 asserts that no reader finds. `Cell` is the same value as `Platform:Cell` in platform configuration and as `cell`
in the infrastructure outputs.

Each root sets all three through the provider's `default_tags`, so a resource added later is tagged
without the author remembering to. `certs.sh` tags in the same API call that imports the
certificate: an untagged certificate is invisible to both the script's own lookup and teardown's
query, and a retry would import a second copy.

## The query is an exact value match

`teardown.sh` and P-05 match `Platform` against `spa-platform` **exactly**. No prefix match, no
wildcard, no `contains`.

This matters because the bootstrap value below begins with the same eleven characters. The tagging
API's `--tag-filters Key=Platform,Values=spa-platform` is already an exact match and is the
supported form; a hand-rolled `Values=spa-platform*` or a client-side `contains(...)` would sweep up
the three resources that are meant to survive, and teardown would then report a failure forever.

## What the tag query proves

- The tagging API's `GetResources` answers for the Region it is asked in. The project's managed SCP
  refuses it outside the selected Region (#118, 2026-10-20), and every root and `certs.sh` create in
  the selected Region (#241), so the query is run there alone.
- It cannot list IAM roles in any Region. IAM roles and customer managed policies are read through
  IAM's own listings, each one's `Platform` tag matched exactly; and the roles `infra/modules/cell`
  declares are confirmed absent by name, which catches a role standing without its tags.
- After a correct destroy it still lists ECS's INACTIVE clusters, services and task definitions, and
  for a while EC2 ids EC2 no longer knows (#118 step 2). An ECS record whose describe says INACTIVE,
  and an EC2 id whose describe does not know it, are not standing resources.

Service-linked roles and AWS's managed roles stand after every teardown; they carry no platform tag
and are not the platform's.

Phase 1 matches the platform tag alone. A second cell adds `Cell` to every reader together
(ADR-0012; #129 Q37).

## The bootstrap exemption

Bootstrap creates three things, and all three are exempt (ADR-0014; the teardown role landed with
task 1.5). That is the written configuration, not evidence of an applied stack.

Resources in `infra/bootstrap` — the Terraform state bucket, the read-only CI plan role and the
teardown role (ADR-0014) — carry a
different `Platform` value and so do not answer the platform query:

| Tag | Value |
| --- | --- |
| `Platform` | `spa-platform-bootstrap` |
| `Cell` | the cell name |
| `Component` | `bootstrap` |
| `Root` | the root the state belongs to |

Those three resources outlive every teardown by design: the plan destroys the three roots in reverse
order and then leaves the state bucket "last, by hand". If they answered the platform query, P-05
could never pass, and a `teardown.sh` that deleted them would destroy the state of the roots it was
in the middle of destroying.

They are removed by hand, not by tag query. Nothing automated deletes them.

## Resources that cannot be tagged

A few AWS resources accept no tags at all. In this stack that includes Cognito app clients, Cognito
resource servers and managed-login branding styles — which is most of what the apps root creates.

The readers are still sufficient, because **every untaggable resource here is a child of a tagged
parent whose deletion removes it**: all three are children of the user pool, which is tagged, and
destroying the pool destroys them.

That is the rule, not a coincidence to rely on. A child counts only if a reader can see its parent:
the cell's inline role policies qualify through the IAM readers, not the tag query. A root that adds
an untaggable resource which is *not* a child of a parent some reader sees makes P-05 vacuous for
that resource, and the readers stop being a proof that the platform's resources are gone.
Workstream 1 files a change request in that case rather than letting the gap sit.

## Consumed by

| Who | How |
| --- | --- |
| Workstream 1 | Applies the tags in every root and in `certs.sh`. Runs `verify-teardown.sh` at the end of `teardown.sh` (task 1.7, #58, #308). |
| Workstream 6 | Runs `verify-teardown.sh` as P-05 (#63), and cites it in the teardown note in task 6.7 (#62) |

A change of value is a one-line edit in `local.tags` per root and in `PLATFORM_TAG_VALUE` in
`tools/scripts/certs.sh` — cheap before the first apply, and a change request after it.
