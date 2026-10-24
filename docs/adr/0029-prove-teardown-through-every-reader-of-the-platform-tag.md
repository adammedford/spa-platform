# ADR-0029: Prove teardown through every API that can read the platform tag, not the tagging query alone

- **Status:** Accepted. Amends ADR-0013's Context (its first sentence) and its Consequences (the
  untaggable bullet). ADR-0013's decision stands.
- **Date:** 2026-10-23
- **Deciders:** integrator, on the confirmed answers to #118 (Q14, Q39 on #129) and #232 Q3
- **Plan ref:** No decision row. P-05 in `docs/plan/04-tests.md` and
  `docs/plan/05-infrastructure-and-cost.md` → Teardown, both frozen exports. Raised by #118, from
  #90's grilling (Q35 on #98); carried out by #308 and change request
  `contracts/changes/issue-308-what-the-tag-query-proves.md`.

## Context

ADR-0013 says teardown proves the account empty by querying one tag. Two records on the platform
project show that one query cannot carry that.

- **#118 step 1 (2026-10-20).** The Resource Groups Tagging API's `GetResources` does not support IAM
  roles in any Region. `modules/cell` creates two roles tagged `Platform = spa-platform`, each with an
  inline policy, and the query ran with both standing and listed neither. In us-east-1 the project's
  managed SCP refused `tag:GetResources` outright.
- **#118 step 2 (2026-10-21).** After a clean destroy of the platform root, the query in the selected
  Region was not empty: ECS keeps INACTIVE clusters and services for a while and deregistered task
  definitions until they are deleted, and the index went on listing a security group and four rules
  EC2 no longer knew. `teardown.sh` exited 1 on a correct teardown.

The contract's child rule did not cover the cell's inline role policies either: their parents are
tagged, but they are roles, which the query cannot return.

## Decision

1. **The proof is that no reader finds what the platform created.** Four readers, in
   `tools/scripts/verify-teardown.sh`:
   - IAM roles, each one's `Platform` tag read through IAM's own API (#129 Q29);
   - IAM customer managed policies, the same way, as a drift guard: the roots create none (#129 Q27);
   - the roles `infra/modules/cell/iam.tf` declares, each confirmed absent by `get-role` (#232 Q3);
   - the tagging API in the selected Region, dropping an ECS record whose describe says INACTIVE and
     an EC2 id EC2's describe does not know (#118 step 2).
2. **The tag readers use one predicate**, `Platform = spa-platform` exactly. Bootstrap stays exempt
   by its value (ADR-0013). Service-linked roles and AWS's managed roles stay because they carry no
   platform tag. The by-name reader holds the cell's exact role names, which its test companion
   holds to `iam.tf`, so it is not a list kept by hand and never matches by prefix.
3. **Every reader retries within the plan's window**, since the tagging index lags a deletion.
4. **The proof is one workstream 1 script.** `teardown.sh` ends by calling it and takes its exit
   status, and workstream 6 runs it as P-05.
5. **us-east-1 is not read.** #241 made `certs.sh` refuse rather than fall back there, and no root
   creates there, so nothing of the platform's can stand there. A root or script that starts
   creating in another Region adds that Region's readers with it.
6. **Phase 1 matches the platform tag alone.** A second cell adds `Cell` to every reader together
   (ADR-0012).
7. **P-05's line in the plan stays as exported.** `contracts/resource-tags.md` states what P-05
   asserts.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Tags alone, through the tagging API and IAM | A role that stands without its tags, untagged by hand or created outside `default_tags`, is invisible to every tag reader. #129's draft rejected names as a hand-kept list; the companion holding the names to `iam.tf` removes that objection, and step 2 put the by-name check on the record. |
| Names alone (ADR-0014's derivation) | Misses a role added later under another name and an orphan from an interrupted apply whose name is not on the list; a tag read through IAM catches both, because `CreateRole` carries the tags. |
| A prefix match on `spa-platform-<cell>-` | Finds bootstrap's `-ci-plan` and `-teardown` roles and fails every run: the IAM form of the wildcard hazard ADR-0013 forbids. |
| ACM and CloudWatch Logs readers in us-east-1 (#129 Q28) | Nothing of the platform's is created there since #241. Revisit if a resource must live in us-east-1. |
| A per-cell IAM path, listed by prefix | Changes `modules/cell`, its tests and the role ARNs to get what a tag read already gives. |
| Resource Explorer | It indexes `iam:role`, but searching creates an index and a service-linked role that the teardown would leave behind. |
| Trusting `terraform destroy` for IAM | P-05 exists to be independent of state, and an orphan from an interrupted apply was never in the state. |
| Editing P-05 in `docs/plan/` | The plan is a frozen export and is never edited in place. |

## Consequences

- A project passes P-05 only when no platform-tagged IAM role or customer managed policy stands, none
  of the cell's roles stands by name, and the selected Region's tag query finds nothing standing.
- A root that adds a resource type no reader can see, or a child whose parent no reader sees, files a
  change request, as the child rule already requires.
- `teardown.sh`'s last step and #63's P-05 run the same script.
- The by-name reader changes with `iam.tf`: a role added there fails the companion until the script's
  list names it.

## Evidence

- The Resource Groups Tagging API reference, Welcome: `GetResources` "does not currently support IAM
  users and roles". Read 2026-10-19.
- The Account Management reference, "Managed policies for your organization": the us-east-1
  `NotAction` lists before and after advanced features.
- Steps 1 and 2's outputs on #118.
- `tools/scripts/verify_teardown_tests.py`, against #118 step 2's records (`d947b75`).
