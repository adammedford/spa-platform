# Change request issue-308: what the tag query proves, and what is proved by name

- **Raised by:** workstream 1, AWS and IaC, during #308
- **Tasks:** #308 (`tools/scripts/verify-teardown.sh`); #118, whose confirmed Q15 (Q40 on #129)
  asked for this request as `issue-118-what-the-tag-query-cannot-see.md`; #58 (1.7, `teardown.sh`);
  #63 (6.9, P-05); #62 (6.7, the teardown note)
- **Date:** 2026-10-23
- **Status:** decided, 2026-10-23, on
  https://github.com/adammedford/spa-platform/issues/232#issuecomment-6375865081.
  All seven items accepted as worded, with ADR-0029; they land with this Status. See the Decision
  section below.

## What changed

`contracts/resource-tags.md` says teardown "proves the account is empty by querying one" tag, and
that the platform tag is "the one `teardown.sh` queries and P-05 asserts returns nothing". Two
records on the platform project show that one query cannot carry that:

- **#118 step 1 (2026-10-20).** The tagging API's `GetResources` does not support IAM roles in any
  Region, and the cell's two roles, `spa-platform-<cell>-task` and `-task-execution`, carry the
  platform tag (`infra/modules/cell/iam.tf`). The query ran with both standing and listed neither.
  In us-east-1 the project's managed SCP refused `tag:GetResources` outright.
- **#118 step 2 (2026-10-21).** After a clean destroy of the platform root the query, in the selected
  Region alone, was not empty: ECS keeps INACTIVE clusters and services for a while and deregistered
  task definitions until they are deleted, and the index went on listing a security group and four
  rules EC2 no longer knew. `teardown.sh` on main exited 1 on a correct teardown.

#308 builds `tools/scripts/verify-teardown.sh`, which `teardown.sh` now ends by calling and whose
exit status is teardown's, and which workstream 6 can run on its own as P-05. Its four readers each
match `Platform = spa-platform` exactly, the contract's rule unchanged:

1. IAM roles by tag: every role, its `Platform` tag read through IAM's own API (#129 Q29).
2. IAM customer managed policies by tag, the same way (#129 Q27): a drift guard, as the roots create
   none.
3. The cell's roles by name: each role `infra/modules/cell/iam.tf` declares, confirmed absent by
   `get-role`, which sees a role that stands without its tags (#232 Q3's recommendation).
4. The tag query in the selected Region, dropping ECS records whose describe says INACTIVE (or
   DELETE_IN_PROGRESS, or unknown) and EC2 ids EC2's describe does not know.

Each reader is retried within the plan's few minutes (`docs/plan/05-infrastructure-and-cost.md`,
Teardown), and the script prints one line per reader and exits non-zero on any match.

## What is asked of the integrator

Prose corrections to `contracts/resource-tags.md`, a change that lands between gates on the
integrator's decision (`contracts/README.md`). The wording below is a proposal; the facts are the
records above.

1. **The opening paragraph**, "Teardown proves the account is empty by querying one of them":
   "Teardown proves that nothing carrying the platform tag remains in the platform project. It reads
   the tag through the tagging API in the selected Region, and through IAM's own API, which the
   tagging API cannot read."
2. **The platform tag's paragraph**, "the one `teardown.sh` queries and P-05 asserts returns
   nothing": "the one every reader of `tools/scripts/verify-teardown.sh` matches exactly, and P-05
   asserts that no reader finds".
3. **A new section, "What the tag query proves"**, after "The query is an exact value match":
   - The tagging API's `GetResources` answers for the Region it is asked in. The project's managed
     SCP refuses it outside the selected Region (#118, 2026-10-20), and every root and `certs.sh`
     create in the selected Region (#241), so the query is run there alone.
   - It cannot list IAM roles in any Region. IAM roles and customer managed policies are read
     through IAM's own listings, each one's `Platform` tag matched exactly; and the roles
     `infra/modules/cell` declares are confirmed absent by name, which catches a role standing
     without its tags.
   - After a correct destroy it still lists ECS's INACTIVE clusters, services and task definitions,
     and for a while EC2 ids EC2 no longer knows (#118 step 2). An ECS record whose describe says
     INACTIVE, and an EC2 id whose describe does not know it, are not standing resources.
4. **The child rule** ("Resources that cannot be tagged"): a child counts only if a reader can see
   its parent. The cell's inline role policies qualify through the IAM readers, not the tag query.
5. **What stays**, one sentence: service-linked roles and AWS's managed roles stand after every
   teardown; they carry no platform tag and are not the platform's.
6. **Consumed by**: workstream 1 runs `verify-teardown.sh` at the end of `teardown.sh` (#58, #308);
   workstream 6 runs it as P-05 (#63) and cites it in the teardown note (#62).
7. **The scope note**: Phase 1 matches the platform tag alone; a second cell adds `Cell` to every
   reader together (ADR-0012; #129 Q37).

ADR-0013's Context opens "Teardown proves the account is empty by querying one tag", and its
Consequences say the untaggable resources leave P-05 holding because each is a child of the tagged
user pool. Both rest on the one query. The ADR amending it is #118 Q14's draft (Q39 on #129,
confirmed), an integrator draft per #232 Q3; it is not written here.

## What #308 did without it

All of the script, its companion `tools/scripts/verify_teardown_tests.py`, and `teardown.sh`'s
call. Nothing in them reads `contracts/resource-tags.md` beyond the two `Platform` values, which do
not change.

## What this request does not ask

#129 Q28 confirmed reading ACM certificates and log groups in us-east-1 through their own APIs. #308
does not build those readers: #241 made `certs.sh` refuse rather than fall back to us-east-1, no
root creates there, and #308's ticket scopes the proof to the selected Region. If the integrator
wants them, the contract's section 3 names them and a workstream 1 ticket adds them to the script.

## Why this is a change request and not an edit

`contracts/` is the integrator's (AGENTS.md, OWNERSHIP.md).

## Blocking

Nothing at runtime: the script matches the value the contract already fixes. Until this lands the
contract describes a proof `teardown.sh` no longer runs.

## Decision

Integrator, 2026-10-23, on Adam's confirmed answer,
https://github.com/adammedford/spa-platform/issues/232#issuecomment-6375865081 (#232 Q3, and this request): **all seven items accepted as worded.**

- Items 1 to 7 land in `contracts/resource-tags.md`: the opening paragraph and the platform tag's
  paragraph as proposed, a new section "What the tag query proves" holding item 3 with items 5 and
  7 at its end, item 4 in "Resources that cannot be tagged", and item 6 in "Consumed by".
- **#232 Q3: both, tag and name.** IAM roles and customer managed policies by tag through IAM's own
  API (#129 Q29), and the cell's roles by name (#118 step 2), as `verify-teardown.sh` already reads
  them. #129's draft rejected names as a list kept by hand; the companion holds the list to
  `iam.tf`, so the objection no longer applies.
- **No us-east-1 readers** (#129 Q28), as "What this request does not ask" proposes: nothing of the
  platform's is created there since #241.
- **ADR-0029 amends ADR-0013**, from #129 Q39's draft reconciled to the two answers above, with a
  status line on ADR-0013.
