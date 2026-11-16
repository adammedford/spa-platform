# What one cell assumes today: what the roots, bootstrap and scripts hold once per project

- **Ticket:** #1239, "What one cell assumes today: an inventory of everything the roots, bootstrap
  and scripts hold once per project", on the map "Wayfinder: P2-8 security cells, a second cell and
  a compromise drill that reads nothing across" (#1237).
- **Date:** 2026-11-16
- **Branch:** `research/p2-8-one-cell-assumptions`, cut from `origin/main` at `adf0273`. Every
  `file:line` below is at that commit.
- **Method:** repository reading, plus hermetic runs: the two teardown test companions as they
  stand, a scratch scenario driven through the repository's own doubles
  (`tools/scripts/cell_doubles.py`) with a second cell standing, `terraform test` in every test
  directory of a `git archive` export, scratch `run` blocks appended to that export's test files,
  and local pattern arithmetic on the IAM resource globs. **Not done:** no `terraform plan` or
  `apply` against AWS, no credential step, no AWS CLI call against the account, no read of the
  deployed cell; the `aws-mcp` connector failed to connect. Terraform on this machine is 1.16.5,
  not the floor 1.11.0 that `.terraform-version` names and CI runs. The pattern arithmetic is a
  local `*`-matches-anything match, not IAM's evaluation. Nothing in the repository was edited;
  the scratch files lived outside it.

Each claim is marked **[executed]** or **[read]**.

## Answer in brief

1. **The cell name runs through resource names, and stops at the edges.** Every AWS resource name
   in `infra/bootstrap`, `modules/cell`, `modules/idp-cognito` and the apps root carries
   `var.cell`, and none collides between `poc` and a second name [read]. What does not follow the
   cell is the layer around the resources: the state keys, the root directories, the outputs
   document, the scripts' paths, the certificate domain, the app definitions and the issuer name.
2. **The state key is the literal `poc/` in five places**, while the state bucket's name follows
   the cell. Planning the platform root with `cell = "poc2"` reads
   `poc/apps/terraform.tfstate` from `spa-platform-poc2-tfstate-<account>` [executed]. With one
   state bucket per cell that is a wrong label; with a shared state bucket, a second cell applied
   from these roots writes over the first cell's state [read].
3. **`infra/envs/` holds one cell by construction, and the scripts hard-code it.**
   `cell-common.sh` fixes the three root directories, `outputs.json` and `images.auto.tfvars`
   under `infra/envs/poc`. Only four scripts take `--cell`, and in `teardown.sh` it moves the
   role, certificate, task-definition family and key-ring names only: the destroy runs in
   `infra/envs/poc/*` whatever cell is named [read].
4. **The charting hypothesis holds: teardown's proof fails for one cell while another stands.**
   Run against the repository's doubles with `poc` torn down and `poc2` standing,
   `verify-teardown.sh --cell poc` exits 1 with two of four readers failing: IAM roles by tag and
   the tag query. The by-name reader, the one reader that is per cell, passes [executed]. This is
   a recorded deferral, not a surprise: ADR-0029 decision 6 and `contracts/resource-tags.md:64`
   both say a second cell adds `Cell` to every reader together [read].
5. **IAM: two statements are prefix globs a second cell's name can fall inside, and eight are
   `*`.** The CI role's `role/spa-platform-<cell>-*` and `policy/spa-platform-<cell>-*` match
   cell `poc-2` or `poc-b` from cell `poc`'s role, and do not match `poc2` [executed, as pattern
   arithmetic]: the hyphen is the delimiter and the cell rule admits inner hyphens. The roles'
   trust condition `aws:SourceArn = arn:…:ecs:<region>:<account>:*` admits every cell's ECS.
   Parameter, bucket, table, log-group and repository grants are exact or slash-delimited and
   match no other cell [executed for the parameter, bucket and log patterns; read for the rest].
6. **Three things exist once per account or per directory and a second bootstrap meets them:**
   the GitHub OIDC provider (default `create = true`, one per account), bootstrap's local state
   (one file per directory, with `prevent_destroy` on the bucket), and the account's `aws/ssm`
   key, under which every cell's client secrets, test-user passwords and key ring are encrypted.
   There is no per-cell KMS key today [read].
7. **The checks assume one cell.** P-01 refuses a second module call and any `count` or
   `for_each` on the cell module call in the platform root, so a gated second cell cannot be a
   second instance in that root without changing P-01. P-02, P-03 and P-07 plan one cell named
   `poc` against literal expectations. `tools/check.sh` and CI find roots by `find infra`, so a
   second root directory is picked up with no edit, but one README test names three paths under
   `envs/poc` [read; the suites run green as they stand, executed].
8. **The cell-name rule is not one rule.** Bootstrap, the platform root and `modules/cell` share
   `^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$`, held together by a test. `modules/idp-cognito` uses
   `^[a-z][a-z0-9-]{1,19}$`, and the apps and identity roots validate nothing. The identity root
   plans `poc-` and `pc`, which the others refuse, and refuses `0a-b`, which the others accept;
   the apps root plans `Not A Cell` [executed].

## 1. What is held once per project and not once per cell

### 1.1 Resources whose names follow the cell (no collision between two cell names) [read]

| Resource | Name | Where |
| --- | --- | --- |
| State bucket | `spa-platform-<cell>-tfstate-<account>` | `infra/bootstrap/main.tf:19-25` |
| CI plan role and policy | `spa-platform-<cell>-ci-plan` | `infra/bootstrap/main.tf:27`, `ci-role.tf:73,337` |
| Teardown role | `spa-platform-<cell>-teardown` | `infra/bootstrap/teardown-role.tf:19` |
| Platform bucket | `spa-platform-<cell>-<account>-<region>-an` | `infra/modules/cell/storage.tf:21-23` |
| VPC, subnets, groups, NAT, endpoints | `spa-platform-<cell>[-…]` | `infra/modules/cell/network.tf:43` and the `Name` tags after it |
| Load balancer, target group | `spa-<cell>` | `infra/modules/cell/alb.tf:14` |
| ECS cluster, log group, task family | `spa-<cell>`, `/spa-platform/<cell>/gateway`, `spa-platform-<cell>` | `infra/modules/cell/ecs.tf:17-19,294` |
| ECR repositories | `spa-<cell>/gateway`, `spa-<cell>/mock-api` | `infra/modules/cell/storage.tf:314` |
| Task and execution roles | `spa-platform-<cell>-task`, `-task-execution` | `infra/modules/cell/iam.tf:101,161` |
| Session table | `spa-platform-<cell>-sessions` | `infra/modules/cell/iam.tf:37`, `sessions.tf:19` |
| Key ring prefix | `/spa-platform/<cell>/data-protection/` | `infra/envs/poc/platform/variables.tf:178-186` |
| User pool, domain prefix | `spa-platform-<cell>`, `spa-platform-<cell>-<account>` | `infra/modules/idp-cognito/main.tf:12`, `infra/envs/poc/idp/main.tf:26-30` |
| Test-user passwords | `/spa-platform/<cell>/test-users/<name>` | `infra/modules/idp-cognito/main.tf:19` |
| App clients, client secrets | `spa-platform-<cell>-<appId>`, `/spa-platform/<cell>/apps/<appId>/client-secret` | `infra/envs/poc/apps/main.tf:69,149` |
| Imported certificate | tagged `Cell = <cell>`, found by domain and that tag | `tools/scripts/certs.sh:519`, `infra/modules/cell/alb.tf:25-34` |

The ECS service is named `gateway` in every cell (`ecs.tf:18`); it is scoped by its cluster, so
it does not collide.

### 1.2 What does not follow the cell

| Held once | Value today | Where | What happens with a second cell |
| --- | --- | --- | --- |
| State keys in the backend blocks | `poc/platform/…`, `poc/apps/…`, `poc/idp/…` | `infra/envs/poc/platform/versions.tf:25`, `apps/versions.tf:29`, `idp/versions.tf:36` | A backend block takes no variable, so the key cannot follow `var.cell` [read]. |
| State keys in the remote-state reads | `poc/apps/terraform.tfstate`, `poc/idp/terraform.tfstate` | `infra/envs/poc/platform/main.tf:51`, `apps/main.tf:24` | With `cell = "poc2"` the read is still `poc/…`, from `spa-platform-poc2-tfstate-…` [executed]. Bootstrap's own outputs print `<cell>/…` keys (`infra/bootstrap/outputs.tf:29-31,46-48`), which the roots then do not use [read]. |
| The root directories | `infra/envs/poc/{platform,apps,idp}` | the tree; `tools/scripts/cell-common.sh:28-30` | One directory holds one `.terraform/` backend binding, one `terraform.tfvars`, one `backend.hcl`. Two cells from one directory means re-initialising between them [read]. |
| Bootstrap's state | local `terraform.tfstate` in `infra/bootstrap` | `infra/bootstrap/README.md:3-17` | One state file per directory. A second apply with another `cell` plans to replace the first cell's bucket and roles; `prevent_destroy` (`state.tf:14-16`) stops the bucket's half [read]. |
| GitHub OIDC provider | created by default | `infra/bootstrap/variables.tf:87-95`, `ci-role.tf:11-12` | One per account; a second bootstrap must set `create_github_oidc_provider = false` and pass the ARN [read; the default of one planned instance executed]. |
| The outputs document | `infra/envs/poc/outputs.json` | `tools/scripts/cell-common.sh:35`; `.gitignore:35`; `.dockerignore:52` | One file. Readers with the path built in: `tools/spactl/Commands.cs:26`, `tests/e2e/Stack/DeployedStack.cs:149` (unless `SPA_E2E_OUTPUTS`), `tools/bench/fargate.py:134`, `tools/image_build_tests.py:102`, the runbooks (`docs/runbooks/README.md:67-68`, `rollback.md:18-19`, `end-app-sessions.md:26`, `release-rollback.md:17`, `secret-rotation.md:23-24`), and the `wiz` skill's sitting files [read]. |
| The applied images | `infra/envs/poc/platform/images.auto.tfvars` | `tools/scripts/cell-common.sh:42` | One file, in the one platform root [read]. |
| The scripts' cell | no option | `seed.sh`, `deploy.sh`, `rollback.sh`, `logs.sh`, `tunnel.sh`, `platform-tfvars.sh` | None takes `--cell` or reads `SPA_CELL`; each acts on whatever `infra/envs/poc` and its `outputs.json` describe. `certs.sh:38`, `teardown.sh:57`, `verify-teardown.sh:61` and `verify-release-immutability.sh:37` default to `poc` and take `--cell` [read]. |
| Certificate domain | `spa-poc.test` | `infra/envs/poc/platform/variables.tf:71`; `certs.sh:39`; `tunnel.sh:79`; `verify-tunnel.sh:35`; `cell-common.sh:590` | Two cells on one domain each import a `*.spa-poc.test` certificate; the `Cell` tag keeps the lookups apart (`alb.tf:17-34`). The local authority's files are per cell (`certs.sh:181-192`) [read]. |
| Tunnel port and host suffix | `8443`, `.spa-poc.test:8443` | `tunnel.sh:72`; `platform/variables.tf:139` | One local port: `tunnel.sh:165-166` refuses a second tunnel on it [read]. |
| App definitions | `fixtures/apps/*.json`, hosts `app1.spa-poc.test:8443`, `app2.…` | `infra/envs/poc/apps/variables.tf:30`; `fixtures/apps/app1.json:3`, `app2.json:3` | A second cell applied from the default gets the same apps on the same hosts; the callback URL is `https://app1.spa-poc.test:8443/signin-oidc` under any cell name [executed]. |
| Issuer name | `cognito-poc` | `tools/scripts/platform-tfvars.sh:61`; `fixtures/apps/app1.json:5` | One name for the one issuer of assumption A4 [read]. |
| VPC range | `10.20.0.0/16` | `infra/envs/poc/platform/variables.tf:36` | Two VPCs may overlap while nothing joins them; it matters only if the cells are ever peered [read]. |
| The project | `AWS_PROFILE=spa-platform`, `AWS_REGION=us-east-2`, one account pin | `tools/aws-project.env`, `tools/preflight.sh:210` | One project, which is the charted boundary. Nothing to change [read]. |
| The encryption key | the account's `aws/ssm` key | `infra/envs/poc/apps/main.tf:143-152`; `infra/modules/idp-cognito/main.tf:133-143`; `infra/envs/poc/platform/variables.tf:188-195` | Shared by every cell. The task role holds no `kms:Decrypt` because that key's policy admits the account's principals through Systems Manager (`infra/modules/cell/iam.tf:188-196`). A drill that tries "cell B's KMS key" has no per-cell key to try [read]. |
| ECR scan setting | per repository on purpose | `infra/modules/cell/storage.tf:321-330` | Already written for more than one cell: the registry setting is one per account and Region, so the module avoids it [read]. |
| Platform tag | `Platform = spa-platform` in every root | `platform/main.tf:25-29`; `apps/main.tf:34-38`; `idp/main.tf:16-20` | The same value for every cell; `Cell` is the tag that differs. See section 3 [read]. |

### 1.3 The cell-name rule [executed]

`infra/bootstrap/tests/root.tftest.hcl:163-213` holds three sources to one expression: bootstrap
(`variables.tf:18`), the platform root (`variables.tf:23`) and `modules/cell` (`variables.tf:18`).
Outside that test: `infra/modules/idp-cognito/variables.tf:6` is `^[a-z][a-z0-9-]{1,19}$`, and
`infra/envs/poc/apps/variables.tf:1-5` and `idp/variables.tf:1-5` have no validation. Scratch
runs in the export: the identity root planned with `cell = "poc-"` and with `cell = "pc"`, and
refused `0a-b` with the module's message; the apps root planned with `cell = "Not A Cell"` and
named its parameter `/spa-platform/Not A Cell/apps/app1/client-secret`. A cell name that the
platform root accepts and the identity module refuses (a leading digit, or 21 or 22 characters)
fails only at the identity root's plan.

## 2. IAM statements with resource `*` or a prefix another cell's name could match

### 2.1 Resource `*` [read]

| Role | Statement | Actions | Where |
| --- | --- | --- | --- |
| CI plan | `DescribeNetworkAndLoadBalancing` | 20 EC2 and ELB describes | `infra/bootstrap/ci-role.tf:130-158` |
| CI plan | `DescribeCompute` | ECS, ECR and Logs describes and lists | `ci-role.tf:162-187` |
| CI plan | `DescribeIdentityProvider` | Cognito describes and lists, with `ListUsers` and `AdminGetUser` | `ci-role.tf:222-247` |
| CI plan | `DescribeCertificates` | three ACM reads | `ci-role.tf:251-262` |
| CI plan | `ListRolesAndPolicies` | `iam:ListRoles`, `iam:ListPolicies` | `ci-role.tf:296-306` |
| CI plan | `DescribeParameters` | `ssm:DescribeParameters` | `ci-role.tf:310-315` |
| Task execution | `EcrAuthorization` | `ecr:GetAuthorizationToken` | `infra/modules/cell/iam.tf:116-121` |
| Task | `SessionManagerMessages` | four `ssmmessages` actions | `infra/modules/cell/iam.tf:217-229` |

Each CI role is one cell's (`spa-platform-<cell>-ci-plan`), and through these six statements it
reads the shape of every cell in the account: networks, services, repositories and image lists,
user pools and their users, certificates, role names and parameter names. All are read-only. The
teardown role has no `*`: both statements name one bucket ARN (`teardown-role.tf:52-63`).

### 2.2 Prefix patterns [executed, as local pattern arithmetic]

| Statement | Pattern for cell `poc` | Matches `poc2` | Matches `poc-2`, `poc-b` | Where |
| --- | --- | --- | --- | --- |
| CI `ReadPlatformRoles` | `role/spa-platform-poc-*` | no | **yes**: its task, execution, CI and teardown roles | `ci-role.tf:267-280` |
| CI `ReadPlatformPolicies` | `policy/spa-platform-poc-*` | no | **yes**: its CI policy | `ci-role.tf:282-294` |
| CI `ReadCellParameters` | `parameter/spa-platform/poc/*` | no | no | `ci-role.tf:317-333` |
| CI `ReadState` | `<state bucket ARN>/*` | not by name | not by name | `ci-role.tf:83-93` |
| Task `ReadClientSecrets` | `parameter/spa-platform/poc/apps/*` | no | no | `infra/modules/cell/iam.tf:27-31,197-207` |
| Task `ReadWriteKeyRing`, `TagKeyRingOnCreate` | `parameter/spa-platform/poc/data-protection` and `/*` | no | no | `iam.tf:45-49,267-318` |
| Task `ReadRegistryAndReleases` | `<bucket ARN>/registry/*`, `/apps/*` | no | no | `iam.tf:177-182` |
| Task `ReadWriteSessions` | the table's ARN and one named index | no | no | `iam.tf:241-261` |
| Execution `PullCellImages` | the two repositories' ARNs | no | no | `iam.tf:123-134` |
| Execution `WriteTaskLogs` | `<log group ARN>:*` | no | no | `iam.tf:139-149` |
| Both roles' trust, `aws:SourceArn` | `arn:…:ecs:<region>:<account>:*` | **yes** | **yes** | `iam.tf:86-94` |
| Bucket policy, delete exemption | the teardown role's looked-up ARN, `ArnNotLike` | no | no | `infra/modules/cell/storage.tf:211-232`; `infra/envs/poc/platform/main.tf:85-92` |

Notes on the three that matter:

- **The two CI globs** are delimited by a hyphen, and `^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$` admits
  inner hyphens. So the brief's pair `poc` and `poc2` does not collide, and `poc` with `poc-2`
  does: cell `poc`'s CI role may read the role and policy documents of cell `poc-2`. The repository
  already names this hazard for the bucket and avoided it there (`ci-role.tf:189-191`,
  `infra/bootstrap/tests/root.tftest.hcl:268`), and `teardown.sh:266-267,285-287` avoids it for
  the task-definition family. The grant is read-only (`iam:Get*`, `iam:List*`). The scratch run
  confirmed the two planned resource strings for cell `poc`.
- **The trust condition** names this account's ECS in this Region and nothing narrower, and
  `infra/modules/cell/tests/module.tftest.hcl:1528-1560` asserts exactly that value. It bounds who
  ECS may act for, not which cluster: a principal able to pass cell B's task role to a task in
  cell A's cluster is not stopped by the trust policy. What stops it is `iam:PassRole`, which no
  role in the repository holds [read].
- **`ReadState`** is exact per bucket. If two cells share one state bucket, as bootstrap's
  `<cell>/…` keys suggest was once intended, each cell's CI role reads the other's state, and the
  identity root's state holds the test-user passwords in clear
  (`infra/modules/idp-cognito/main.tf:95-98`) [read].

Outside IAM but on the same question: the two-task cell's S3 and DynamoDB gateway endpoints carry
AWS's default policy (`infra/modules/cell/network.tf:268-278`), and the task's group sends 443 to
`0.0.0.0/0` (`network.tf:156-163`), so the network places no bound on which bucket or table a
task reaches; IAM alone does [read].

## 3. Does teardown's proof pass for one cell while another stands?

**No. It fails, and the repository already says it would.**

What the script does [read]: `tools/scripts/verify-teardown.sh` runs four readers.

| Reader | Predicate | Cell in the predicate? | Where |
| --- | --- | --- | --- |
| 1. IAM roles by tag | every role whose `Platform` tag equals `spa-platform` | no | `verify-teardown.sh:138-157` |
| 2. IAM customer managed policies by tag | the same | no | `verify-teardown.sh:159-162` |
| 3. The cell's roles by name | `spa-platform-<CELL>-task-execution`, `-task` | **yes** | `verify-teardown.sh:71,166-181` |
| 4. The tag query in the Region | `--tag-filters Key=Platform,Values=spa-platform` | no | `verify-teardown.sh:376-380` |

ADR-0029 decision 6: "Phase 1 matches the platform tag alone. A second cell adds `Cell` to every
reader together (ADR-0012)." `contracts/resource-tags.md:64` repeats it. The glossary's
**Teardown** is "removing a cell from the platform project … ending with proof that nothing
carrying the platform tag remains" (`GLOSSARY.md:83`), which with two cells is a proof about the
project, not about the cell.

What was run [executed]: a scratch script imported `cell_doubles.py` and
`verify_teardown_tests.py` unchanged, took the companion's `torn_down_world()` (cell `poc` as
#118 step 2 left it) and added a standing `poc2`: its two cell roles tagged
`Platform = spa-platform, Cell = poc2`, its two bootstrap roles under the bootstrap value, and a
bucket and a load balancer in the tag index under `Cell = poc2`.

- Control, `poc` torn down and no other cell, `--cell poc`: exit 0, four readers pass.
- `poc` torn down, `poc2` standing, `--cell poc`: **exit 1, "2 of 4 readers still find something
  carrying Platform=spa-platform"**. IAM roles by tag names `spa-platform-poc2-task` and
  `spa-platform-poc2-task-execution`; the tag query names `poc2`'s bucket and load balancer. The
  by-name reader passes for `poc`, and the policies reader passes. The tag query sent was
  `Key=Platform,Values=spa-platform` with no `Cell` filter.
- The same world, `--cell poc2`: exit 1, three of four readers fail, as it should.

So `teardown.sh`, which ends on this script and takes its exit status (`teardown.sh:339-342`),
exits 1 on a correct teardown of one cell while another stands. This was run against the doubles,
not against AWS.

Three further points from reading `teardown.sh`:

- **Step 3 is already per cell.** The certificate is found by `Platform` and `Cell` together
  (`teardown.sh:258-261`), the task-definition family is matched exactly (`:269-288`), and the
  ring prefix carries the cell and its trailing slash (`:307-326`). `teardown_tests.py:188-193`
  and `:221` hold that another cell's task definitions and ring are left [executed: the companion
  ran green, 30 tests].
- **Steps 1 and 2 are not.** The bucket comes from the platform root's state and the destroys run
  in `infra/envs/poc/{platform,apps,idp}` (`teardown.sh:126,214-221`; `cell-common.sh:28-30`).
  `--cell poc2` therefore assumes `poc2`'s teardown role and deletes `poc2`'s certificate, family
  and ring, while destroying whichever cell the `poc` directories are initialised against. Nothing
  compares `--cell` with the roots' `cell` output [read].
- **The caller check is cell-blind on purpose**: `spa-platform-*-teardown` refuses a session of
  any cell's teardown role (`cell-common.sh:111-113`) [read].

## 4. What `tools/check.sh`, P-01 to P-03 and `terraform test` assume about the number of cells

**`tools/check.sh` and CI** [read]. Both discover rather than list: `find infra -name '*.tf'` for
validate (`tools/check.sh:642`, `.github/workflows/ci.yml:806`) and
`find infra -type f -name '*.tftest.hcl'` for tests (`tools/check.sh:659`, `ci.yml:824`). A
second root directory under `infra/envs/` is formatted, validated and tested with no edit. CI
holds no AWS credentials (ADR-0016), so nothing in CI assumes or reads a cell.

**P-01, the platform plan allowlist** [read; suite executed green, 24 runs].
`infra/envs/poc/platform/tests/root.tftest.hcl`:

- `:733-740` holds the root's module calls to exactly `[["cell", "../../../modules/cell"]]`.
- `:742-750` refuses `count` or `for_each` on the call: "either multiplies every resource in it,
  so one ALB becomes two."
- `:925-946` holds one ALB, one listener, one target group, one service and one bucket, each one
  declaration with no `count` or `for_each`.

P-01's sentence is "exactly one ALB … one bucket" per platform plan. A second cell as a second
module instance in this root, gated or not, fails the first two assertions. A second cell as a
second root, or as the same root applied with another `cell` and another state, leaves P-01 true
of each plan.

**P-02, the apps plan allowlist** [read; executed green, 10 runs].
`infra/envs/poc/apps/tests/root.tftest.hcl:374-409` plans one root with the fixture definitions
and one more. It says nothing about which cell an app belongs to: the definitions directory is
one list for the one cell (section 1.2).

**P-03, the task role's statements** [read; executed green, 75 runs in `modules/cell`].
`infra/modules/cell/tests/module.tftest.hcl` plans the module once with `cell = "poc"` (`:210`).
It has one cross-cell probe: the client-secret path must not admit
`/spa-platform/another-cell/apps/app1/client-secret` (`:1236-1245`). Runs `:2147` and `:2158`
refuse another cell's ring prefix and a wider one. There is no like probe for the bucket, the
session table, the log group or the repositories; those grants name the module's own resources'
ARNs, so they hold by construction, and no run plans two cells and compares them. The trust
assertion at `:1528-1560` pins the account-wide `ecs:…:*` value.

**P-07, the identity plan allowlist** [read; executed green, 5 runs]. One pool, one domain
(`infra/envs/poc/idp/tests/root.tftest.hcl:181-189`), planned with `cell = "poc"`.

**Bootstrap's tests** [read; executed green, 28 runs]. They plan `cell-7` as well as `poc`
(`infra/bootstrap/tests/root.tftest.hcl:310-365`), so the derivations are exercised for a second
name. `infra/bootstrap/tests/recipes.tftest.hcl:43-57` names three README paths, two of them
under `envs/poc`; the rules themselves glob every README under `infra/` (`:10-12`).

**The companions** [executed]. `verify_teardown_tests.py` (44 tests) and `teardown_tests.py`
(30 tests) pass. The doubles' world is one cell: `cell_doubles.py:62` sets `CELL = "poc"`, and a
second cell appears only as stray records (`teardown_tests.py:67`,
`verify_teardown_tests.py:153-156`).

## What was executed and what was only read

All on 2026-11-16, at `adf0273`.

**Executed**

- `python3 -I tools/scripts/verify_teardown_tests.py`: 44 tests, OK.
- `python3 -I tools/scripts/teardown_tests.py`: 30 tests, OK.
- A scratch script, outside the repository, driving `tools/scripts/verify-teardown.sh` through
  `tools/scripts/cell_doubles.py` with a second cell standing: the three results in section 3.
  The doubles answer for AWS; no call left the machine.
- `terraform test` (Terraform 1.16.5, mock providers, `init -backend=false`) in a `git archive`
  export of `infra`, `contracts`, `fixtures`, `tools` and `docs/plan`: bootstrap 28, apps 10,
  identity 5, platform 24, `modules/cell` 75, `modules/idp-cognito` 8, all passing. `init`
  fetched providers from the registry; nothing reached AWS.
- Scratch `run` blocks appended to the export's test files only: the platform root with
  `cell = "poc2"` (remote-state key `poc/apps/terraform.tfstate`, bucket
  `spa-platform-poc2-tfstate-<account>`, teardown role `spa-platform-poc2-teardown`); the apps
  root with `cell = "Not A Cell"`; the identity root with `poc-`, `pc` (planned) and `0a-b`
  (refused by the module; the 22-character case was skipped after that failure and is read from
  the expression); bootstrap's planned `ReadPlatformRoles`, `ReadPlatformPolicies` and
  `ReadCellParameters` resources and its one planned OIDC provider.
- Local pattern arithmetic on the globs in section 2.2 against cells `poc`, `poc2`, `poc-2`,
  `poc-b`, `pocket` and `dev`. Not IAM's evaluation.

**Only read**

- `infra/bootstrap/` (`main.tf`, `state.tf`, `ci-role.tf`, `teardown-role.tf`, `variables.tf`,
  `outputs.tf`, `README.md`, `terraform.tfvars.example`, `tests/`).
- `infra/envs/poc/{platform,apps,idp}` (every `.tf`, the examples, the test files' run lists and
  the P-01 runs in full).
- `infra/modules/cell` (`iam.tf`, `storage.tf`, `sessions.tf` in full; `ecs.tf`, `alb.tf`,
  `network.tf`, `variables.tf` by their names, scopes and the ranges cited) and
  `infra/modules/idp-cognito`.
- `tools/scripts/verify-teardown.sh`, `teardown.sh` and `cell-common.sh:1-330` in full; the other
  scripts by search for the cell, the roots, the domain and the outputs path.
- `tools/check.sh:605-676`, `.github/workflows/ci.yml:760-840`, `tools/aws-project.env`,
  `tools/preflight.sh:200-294`.
- ADR-0012, ADR-0013, ADR-0029 in full; ADR-0014, ADR-0016, ADR-0025 by their decisions;
  `GLOSSARY.md:11-14,81,83`; `contracts/resource-tags.md`.
- The consumers of `infra/envs/poc/outputs.json`, by `git grep`.
- Whether the statements in section 2.1 behave as written in the account, what the project's
  managed IAM adds or refuses, and anything about the deployed cell: not read, not run.
