# `infra/bootstrap` — Terraform state and the CI plan role

Task 1.0, and the teardown role from change request 1.5. This root is applied
**once**, with **local state**, and owns three things and nothing else:

1. The Terraform state bucket: private, versioned, TLS only, public access
   blocked, encryption on.
2. The read-only role CI assumes to run plans.
3. The teardown role, the one principal allowed to delete under `apps/` in the
   platform bucket (ADR-0014, which amends the plan's "Never contains: Anything
   else" boundary for this root).

It keeps local state because it creates the bucket that holds remote state, so it
cannot keep its state there. `.gitignore` keeps `*.tfstate*` out of the
repository; keep the file on the workstation that applied it. These resources
outlive every teardown — `teardown.sh` destroys the three platform roots and then
the state bucket goes last, by hand.

## Every other root uses the S3 backend with its native lock file

Terraform 1.11 gave the S3 backend `use_lockfile`, which puts the lock in an object
beside the state instead of in a DynamoDB table. The plan chose it deliberately
(`docs/plan/01-plan.md`, "Defaults taken"), so **no DynamoDB table exists anywhere
in this platform**. `required_version` here is `>= 1.11.0` for that reason; the
workstation checked on 11 October 2026 ran 1.15.8.

One key per root, so one lock per root:

| Root | Key |
| --- | --- |
| `infra/envs/poc/idp` | `poc/idp/terraform.tfstate` |
| `infra/envs/poc/apps` | `poc/apps/terraform.tfstate` |
| `infra/envs/poc/platform` | `poc/platform/terraform.tfstate` |

Each of those roots carries a block of this shape. The `backend_blocks` output
prints all three, filled in, after the apply:

```hcl
terraform {
  backend "s3" {
    bucket       = "spa-platform-poc-tfstate-111122223333"
    key          = "poc/platform/terraform.tfstate"
    region       = "us-east-1"
    encrypt      = true
    use_lockfile = true
  }
}
```

A backend block takes no variables, so the bucket name is written out. Either paste
it, or keep the three values in a `backend.hcl` and run
`terraform init -backend-config=backend.hcl`.

## Applying it

```bash
cd infra/bootstrap
cp terraform.tfvars.example terraform.tfvars   # then edit
terraform init                                 # local backend, no bucket needed
terraform plan
terraform apply
terraform output -json
```

The apply fails, rather than creating a role nobody can assume, if neither
`github_repository` nor `additional_trusted_principal_arns` is set, or if
`teardown_trusted_principal_arns` is empty. `terraform test` here runs against a
mock provider and needs no credentials.

## Naming

Every resource name carries the cell name (ADR-0012):

| Resource | Name |
| --- | --- |
| State bucket | `spa-platform-<cell>-tfstate-<account id>` |
| CI plan role and its policy | `spa-platform-<cell>-ci-plan` |
| Teardown role and its inline policy | `spa-platform-<cell>-teardown` |

The cell is 3 to 22 lowercase letters, digits or hyphens with no hyphen at either
end, `^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$`, the same expression the platform root and
`modules/cell` use (change request 1.5). 3 is the frozen appId minimum the cell is
published as; 22 keeps the load balancer's `spa-<cell>` within 32 characters.
`tests/root.tftest.hcl` reads all three sources and fails if they drift.

The account id is appended to the bucket name because S3 names are globally unique
and a fixed name would collide between accounts. Set `state_bucket_name` to
override it.

### Tags, and why bootstrap's differ

The platform tag that `teardown.sh` queries, and that test P-05 asserts returns
nothing, is `Platform = spa-platform`. Resources in this root carry
`Platform = spa-platform-bootstrap` instead, with `Cell`, `Component = bootstrap`,
`ManagedBy` and `Root`. That is deliberate: the state bucket and the two roles
survive every teardown, so if they answered the platform query, P-05 could never
pass. The value is set in one place, `local.tags` in `main.tf`.

## The CI plan role

`sts:AssumeRoleWithWebIdentity` from the GitHub Actions OIDC provider, with both
the `aud` and the `sub` claim pinned. The `sub` claim is what ties the role to this
repository and these refs; without it, any workflow in the world could assume it.
Named principals (a deployer's role, a runner elsewhere) go in
`additional_trusted_principal_arns` and use plain `sts:AssumeRole`.

The permissions are read-only and listed in `ci-role.tf`, one statement per thing
a plan reads: the state, the network and load balancer, the compute, the platform
bucket, the identity provider, the imported certificate, the platform's roles and
policies by name, and Parameter Store under this cell's path. Task 1.9 asserts on
permissions, so a statement added here should fail review before it fails a test.

Two choices in that policy are worth knowing about.

**CI plans run with `-lock=false`.** A role that can write nothing cannot take a
lock, and `use_lockfile` makes the lock an object write. The policy grants no write
at all by default:

```bash
terraform plan -lock=false -out=plan.tfplan
```

Set `allow_state_lock = true` to let CI block a concurrent apply. It adds one
statement, `s3:PutObject` and `s3:DeleteObject` on keys matching `*.tflock` in the
state bucket only, so it still cannot write a state file.

**`plan_can_read_parameters` defaults to true, and that lets a CI plan read this
cell's secrets.** Terraform's `aws_ssm_parameter` reads a parameter's value in
order to plan it, so without the permission the apps root's plan fails on the
client-secret parameters and the generated test-user passwords. The grant is
scoped to `/spa-platform/<cell>/*`. Set it false and accept an incomplete plan if
that trade is the wrong way round for the account.

## The teardown role

`spa-platform-<cell>-teardown` (ADR-0014). The platform bucket's policy denies
`s3:DeleteObject` and `s3:DeleteObjectVersion` under `apps/` to every principal but
this one, which is half of what makes a release prefix write-once (P-04). It lives
here because it must outlive the teardown it permits.

- **Permissions:** `s3:DeleteObject` and `s3:DeleteObjectVersion` on
  `<platform bucket ARN>/apps/*`, and `s3:ListBucket` and `s3:ListBucketVersions` on
  the bucket. The bucket ARN is one bucket's, `spa-platform-<cell>` by default or
  `platform_bucket_name`, never a glob. **No write, ever**: a conditional write
  succeeds over a delete marker, so a principal holding both could replace a
  release in two steps.
- **Trust:** `teardown_trusted_principal_arns`, its own required list. IAM users
  assume the role only with MFA (`Bool`, so long-term keys alone fail). Role ARNs
  are allowlisted federated human roles, in a separate statement, whose identity
  provider enforces MFA. The input refuses the account root, STS session ARNs and
  the CI plan role; the GitHub OIDC provider is never trusted.
- **Who reads its ARN:** nobody, by design. The platform root looks the role up by
  its derived name, and `teardown.sh` derives the same name, because this root's
  state is local. `teardown_role_arn` is an output for humans.

The role is break-glass: whoever can assume it can delete a published release.
P-04 never runs as it.

## Verifying the lock — the task's "done when"

> A second apply started during the first is refused by the lock.

That is a property of the **S3 backend**, so it is proved on a root that uses the
backend, not on this one, which keeps local state. The soonest it can be checked is
right after the first root is initialised against the backend (task 1.1). Two
terminals, in the same root:

```bash
# Terminal 1: an apply slow enough to still be holding the lock.
cd infra/envs/poc/idp
terraform apply -auto-approve

# Terminal 2, while terminal 1 is still running:
cd infra/envs/poc/idp
terraform apply -auto-approve -lock-timeout=0
```

The second command must exit non-zero with `Error acquiring the state lock` and
`ConditionalRequestConflict`, naming the lock's ID, the operation and who holds it.
`-lock-timeout=0` makes it fail at once instead of waiting.

The lock object itself can be seen while the first apply runs:

```bash
aws s3api list-objects-v2 \
  --bucket "$(terraform -chdir=../../../bootstrap output -raw state_bucket)" \
  --prefix poc/idp/ --query 'Contents[].Key'
# poc/idp/terraform.tfstate
# poc/idp/terraform.tfstate.tflock   <- present only while a lock is held
```

A lock left behind by a killed process is released with
`terraform force-unlock <ID>`, using the ID the error printed.

## What this root never contains

Anything else. The boundary is in `docs/plan/05-infrastructure-and-cost.md`:
`bootstrap` changes once, and every other resource belongs to `envs/poc/idp`,
`envs/poc/apps` or `envs/poc/platform`.
