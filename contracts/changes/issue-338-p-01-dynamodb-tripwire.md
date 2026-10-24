# Change request issue-338: P-01's DynamoDB tripwire, conditional on `session_store`

- **Raised by:** workstream 1, AWS and IaC, during #338 (continuity 9/13, the spec #332's T9)
- **Tasks:** #338; #332's G6 (S8) spec, whose T9 row says "P-01's `aws_dynamodb_table` tripwire
  becomes conditional on the variable"; #347 (T10), which builds on the same cell; P2-1
- **Date:** 2026-10-24
- **Status:** open

## The gap

#338 asks for the session table in `infra/modules/cell` behind `session_store = "dynamodb"`, and for
this request, on the reading that "the table is created only when the variable asks for it, so the
existing policy stays green on the default". That reading does not hold. P-01 reads the cell
module's **source**, not its plan, so a table declared with `count = 0` on the default inputs fails
it as surely as one that is always created. Three assertions in
`infra/envs/poc/platform/tests/root.tftest.hcl` refuse the declaration:

1. **The tripwire** (`the_platform_plan_holds_one_alb_listener_target_group_service_and_bucket_and_no_nat_gateway_instance_or_database`,
   lines 835-841). Its regex lists `dynamodb_.+` among the database prefixes, matched against every
   `resource "<type>"` line of the module's `*.tf` files. The comment above it (lines 807-809) says
   why: "`aws_dynamodb_table` among them is a Phase 1 tripwire, because Session.Store admits dynamodb
   only in Phase 2 (Q81 ...)".
2. **The count/for_each closure** (`the_platform_plan_holds_exactly_the_allowlisted_types_and_counts`,
   lines 741-751). Only `aws_subnet.public`, `aws_route_table_association.public` and
   `aws_ecr_repository.repository` may carry `count` or `for_each`.
3. **The type-count map** (the same run, lines 753-794). A type it does not name fails it.

Tried on #338's branch on 2026-10-24: with the table declared (`count =
local.session_store_is_dynamodb ? 1 : 0`) the platform root's suite ran 18 passed and 2 failed,
exactly runs 1 and 2-3 above. Without it, 20 passed.

There is no route round the source read, and none is wanted: the module's own tests refuse a
`.tf.json` file and a child module (`infra/modules/cell/tests/module.tftest.hcl`, the resource-set
run), and the root's refuse a resource of its own. Changing what P-01 proves is the integrator's
decision (ADR-0016 makes plan policy an assertion on the configuration; #338 says so), so the table
waits on this request and #338 lands without it.

## What #338 landed without it

On branch `task/issue-338-continuity-9-13-the-cell-s-table-ring`, all in workstream 1's paths, every
piece behind an input whose default is Phase 1's:

- `data_protection_parameter_prefix` (empty, or exactly `/spa-platform/{cell}/data-protection/`) and
  `data_protection_kms_key_id` (empty, or the API's `KeyId` pattern; needs the prefix);
- the validation refusing `session_store = "dynamodb"` with no prefix, naming both inputs;
- the task role's `ReadWriteSessions` (the store's five actions on
  `table/spa-platform-{cell}-sessions` and its `index/appId`), `ReadWriteKeyRing` and
  `TagKeyRingOnCreate` statements, built from the table's **name**, since there is no table resource
  to read an ARN from;
- `Session__TableName`, `DataProtection__ParameterPrefix` and `DataProtection__KmsKeyId` in the task
  environment;
- `teardown.sh`'s deletion of the ring prefix, and `verify-teardown.sh`'s judgement of a listed table.

So a cell set to `dynamodb` today plans grants and an environment naming a table that does not
exist. No cell is set to it before G6, and the gateway cannot use it before the store ticket (#344)
lands; the table is what this request unblocks.

## The change

All three edits are to `infra/envs/poc/platform/tests/root.tftest.hcl`, in one commit with the table
itself. The type stays a tripwire on the default inputs, and becomes admissible only in the one
declaration, with the one `count`, that the variable gates.

### 1. The tripwire admits exactly the gated session table

The forbidden-type list keeps `dynamodb_.+`. One declaration is exempted, by type, name and the
exact `count` line, and the run gains a plan assertion that on the default inputs it is no instance:

```hcl
  # The one exemption: the session table, declared once with exactly this count, so that only
  # session_store = "dynamodb" creates it (#338). Any other DynamoDB declaration, or this one with any
  # other count, is still a database in the platform plan.
  assert {
    condition = [
      for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) : m[0]
      if can(regex("^aws_(nat_gateway|...|dynamodb_.+|...)$", m[0]))
      && !(m[0] == "aws_dynamodb_table" && m[1] == "sessions"
           && length(regexall("(?m)^  count\\s*=\\s*local\\.session_store_is_dynamodb \\? 1 : 0$", m[2])) == 1)
    ] == []
    error_message = "(as today)"
  }

  # And on the default inputs, which are Phase 1's, the exempted table plans no instance.
  assert {
    condition     = length(aws_dynamodb_table.sessions) == 0
    error_message = "P-01: on the default inputs the platform plan holds no session table. Only session_store = \"dynamodb\" creates one."
  }
```

(`...` is the existing list, unchanged.) `local.session_store_is_dynamodb` is
`var.session_store == "dynamodb"`, in `infra/modules/cell/iam.tf`.

### 2. The count/for_each closure names the table

`"aws_dynamodb_table.sessions" = aws_dynamodb_table.sessions` joins the three keys of the first
assertion's map.

### 3. The type-count map counts it at its planned instances

`"aws_dynamodb_table.sessions" = length(aws_dynamodb_table.sessions)` joins the lookup map, and
`aws_dynamodb_table = 0` the expected one, since the run plans the default inputs. A second run on
`session_store = "dynamodb"` with the cell's prefix, expecting `aws_dynamodb_table = 1` and every
other count unchanged, proves the gate opens to one table and nothing else.

## Why the default stays green

On the default inputs `local.session_store_is_dynamodb` is false, the table's `count` is 0, and the
plan holds no DynamoDB resource: the new plan assertion in 1 says so, and the type-count map in 3
counts 0. Every other declaration is unchanged, and no other database type gains an exemption. What
P-01 stops proving is only that the module's **source** names no DynamoDB table; what it goes on
proving is that the Phase 1 **plan** holds none, which is P-01's own sentence: "No NAT gateway, database or EC2 instance" in the platform plan (`docs/plan/04-tests.md:76`).

## The table, for the session that lands it

`infra/modules/cell/sessions.tf`, tried against the module's mock provider on 2026-10-24 (aws
provider 6.70.0 as the module resolved it; the roots pin 6.68.0, where the landing session confirms
`global_secondary_index.key_schema`: 6.70.0 deprecates the index's `hash_key` for it):

```hcl
resource "aws_dynamodb_table" "sessions" {
  count = local.session_store_is_dynamodb ? 1 : 0

  name         = local.session_table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "pk"

  attribute {
    name = "pk"
    type = "S"
  }

  attribute {
    name = "appId"
    type = "S"
  }

  global_secondary_index {
    name            = local.session_table_index_name
    projection_type = "KEYS_ONLY"

    key_schema {
      attribute_name = "appId"
      key_type       = "HASH"
    }
  }

  ttl {
    attribute_name = "expiresAt"
    enabled        = true
  }

  tags = { Name = local.session_table_name }
}
```

With it, the same commit: `ReadWriteSessions` reads `aws_dynamodb_table.sessions[0].arn` (and
`/index/appId`) instead of the name-built `local.session_table_arn`; `aws_dynamodb_table` joins the
module's own resource-type allowlist (`infra/modules/cell/tests/module.tftest.hcl`, the first run);
and the module suite gains a run on `dynamodb` asserting `pk`, the `appId` index, time to live on
`expiresAt` and `PAY_PER_REQUEST`, and one on the defaults asserting no instance. Both ran green in
the trial. Encryption stays DynamoDB's default (#332 D6), and there is no index on `subject` (G7's,
ADR-0028).

## P-03's sentence, for the same decision

P-03 reads "Read on this cell's bucket prefixes and client-secret parameter path, the four
`ssmmessages` permissions, and nothing else. No write to the registry." (`docs/plan/04-tests.md:78`).
Its runs plan the default inputs, so they stay green, and on those inputs the sentence still holds.
Off them it does not, and this section records the finding of #353 (raised from #350's review
record) here rather than in a second request (#349 Q17).

Two inputs widen the task role, and they are independent conditions in `infra/modules/cell/iam.tf`,
as the store and the ring are in the gateway (P2-2 decision 5):

- `session_store = "dynamodb"` (`local.session_store_is_dynamodb`) adds `ReadWriteSessions` (P2-1
  Decision 7);
- a non-empty `data_protection_parameter_prefix` (`local.key_ring_enabled`) adds `ReadWriteKeyRing`
  and `TagKeyRingOnCreate` (P2-2 decision 12, #274), whichever the store.

So a memory-store cell with a ring prefix changes P-03 as surely as a `dynamodb` one. The fourth
combination does not plan: `var.session_store`'s validation refuses `dynamodb` with no prefix
(`dynamodb_with_no_ring_prefix_is_refused_at_plan`). That leaves three cases:

| Case | Inputs | The task role's statements | The run that holds the set |
| --- | --- | --- | --- |
| Default | `memory`, no prefix | `ReadRegistryAndReleases`, `ReadClientSecrets`, `SessionManagerMessages`: P-03's sentence | `under_memory_the_cell_is_phase_1_s_unchanged` |
| Memory plus ring | `memory`, the cell's prefix | the default's three, `ReadWriteKeyRing`, `TagKeyRingOnCreate` | `a_ring_under_memory_carries_the_ring_and_no_table` |
| DynamoDB plus ring | `dynamodb`, the cell's prefix | the default's three, `ReadWriteSessions`, `ReadWriteKeyRing`, `TagKeyRingOnCreate` | `under_dynamodb_the_task_role_holds_exactly_the_table_and_ring_grants` |

Each grant, as `iam.tf` builds it, with `{partition}`, `{region}` and `{account}` the cell's own and
the prefix held by its validation to exactly `/spa-platform/{cell}/data-protection/`:

- **`ReadWriteSessions`**, behind `session_store`: `dynamodb:GetItem`, `dynamodb:PutItem`,
  `dynamodb:DeleteItem`, `dynamodb:Query` and `dynamodb:BatchWriteItem`, on
  `arn:{partition}:dynamodb:{region}:{account}:table/spa-platform-{cell}-sessions` and that ARN's
  `/index/appId`. The index is named, not `index/*`; no `Scan`, no `UpdateItem`, nothing of the
  table's administration, and no other statement carries a DynamoDB action.
- **`ReadWriteKeyRing`**, behind the prefix: `ssm:PutParameter` and `ssm:GetParametersByPath`, on
  `arn:{partition}:ssm:{region}:{account}:parameter/spa-platform/{cell}/data-protection`, the
  prefix's own ARN with no trailing slash, and on that ARN's `/*`.
- **`TagKeyRingOnCreate`**, behind the prefix: `ssm:AddTagsToResource` on the ring's children,
  `arn:{partition}:ssm:{region}:{account}:parameter/spa-platform/{cell}/data-protection/*`, alone,
  conditioned by `StringEquals` on `aws:RequestTag/Platform` = `spa-platform`,
  `aws:RequestTag/Cell` = the cell and `aws:RequestTag/ManagedBy` = `gateway`, and by
  `ForAllValues:StringEquals` on `aws:TagKeys` = `Platform`, `Cell`, `ManagedBy`.

In none of the three cases does the role write to the registry: the policy's one S3 action is
`ReadRegistryAndReleases`'s `s3:GetObject`, unconditional, on the `registry/*` and `apps/*`
prefixes and not the bucket's own ARN, so P-03's second sentence holds as written. Nor does it
delete a key: no statement grants `ssm:DeleteParameter` or `ssm:DeleteParameters` (P2-2 decision
12), and `teardown.sh` deletes the ring as the operator.

The DynamoDB-plus-ring run holds all three grants to the actions, resources and conditions above,
the absence of a key deletion, and the role's closures (Allow only, no wildcard action, only the
tunnel's grant on `"*"`, no `NotAction` or `NotResource`). The memory-plus-ring run holds the set of
statements only; the ring's two are the same `dynamic "statement"` blocks on `local.key_ring_enabled`
whichever the store, so the DynamoDB run's assertions on them are the ring's in both cases.

Nothing in this request edits P-03. It asks the integrator, deciding the tripwire, to record P-03's
Phase 2 reading beside it: the sentence on the default inputs, and behind it, per case, the grants
above, held by the runs named.

## Why this is a request rather than an edit

`root.tftest.hcl` is under `infra/`, workstream 1's path, but what P-01 proves is a plan-policy
decision (ADR-0016), and #338 names it the integrator's. The edit is three assertions and one map
entry; the decision is whether P-01 may read a gated declaration as no database.

## Blocking

#338's table acceptance clauses (`pk`, the `appId` index, time to live, on-demand, and "no table,
no index" under `memory` as a plan fact rather than a source fact) wait on this request. Nothing else
of #338 does. #344's store can be built and proved against its fake without the table; the deployed
G6 run (#348) cannot.
