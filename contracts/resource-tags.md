# Resource tags

- **Freezes at:** G1a
- **Consumed by:** workstream 1, `teardown.sh`, and workstream 6's P-05
- **Source:** change request `changes/1.6a-platform-tag-convention.md`; ADR-0013. Not
  `docs/plan/02-contracts.md`: the plan names the platform tag three times without defining it, so
  this file has no plan section behind it.

Every AWS resource this platform creates carries the same three tags. Teardown proves the account is
empty by querying one of them, so the value workstream 1 applies and the value workstream 6 queries
have to be the same string. This file is that string.

## The tags

| Tag | Value | Applied to |
| --- | --- | --- |
| `Platform` | `spa-platform` | Every resource in `infra/envs/poc/{idp,apps,platform}`, and the wildcard certificate `tools/scripts/certs.sh` imports to ACM |
| `Cell` | the cell name, e.g. `poc` | The same resources |
| `ManagedBy` | `terraform`, or `certs.sh` for the certificate | The same resources |

`Platform = spa-platform` is **the platform tag**: the one `teardown.sh` queries and P-05 asserts
returns nothing. `Cell` is the same value as `Platform:Cell` in platform configuration and as `cell`
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

## The bootstrap exemption

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

The tag query is still sufficient, because **every untaggable resource here is a child of a tagged
parent whose deletion removes it**: all three are children of the user pool, which is tagged, and
destroying the pool destroys them.

That is the rule, not a coincidence to rely on. A root that adds an untaggable resource which is
*not* a child of a tagged parent makes P-05 vacuous for that resource, and the tag query stops being
a proof that the account is empty. Workstream 1 files a change request in that case rather than
letting the gap sit.

## Consumed by

| Who | How |
| --- | --- |
| Workstream 1 | Applies the tags in every root and in `certs.sh`. Queries the platform tag at the end of `teardown.sh` (task 1.7). |
| Workstream 6 | Asserts the query returns nothing (P-05), and walks the teardown note in task 6.7 |

A change of value is a one-line edit in `local.tags` per root and in `PLATFORM_TAG_VALUE` in
`tools/scripts/certs.sh` — cheap before the first apply, and a change request after it.
