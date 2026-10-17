# `modules/cell`

One cell: the deployment unit. A Phase 2 cell is one more instance of this module
(ADR-0012), which is why nothing here is named for the proof of concept and every
name carries `var.cell`.

## What is here

| File | Task | Holds |
| --- | --- | --- |
| `network.tf` | 1.2 | The VPC, two public subnets in two zones, the internet gateway, one route table, the two security groups, and the VPC's default security group, adopted with no rule |
| `storage.tf` | 1.5 | The platform bucket and its policy, and the two image repositories |
| `alb.tf` | 1.3 | The internal load balancer, its one HTTPS listener, its one target group, and the lookup of the imported wildcard certificate |
| `ecs.tf` | 1.4a | The ECS cluster, the log group, the task environment, the two container definitions, the task definition and the service |
| `iam.tf` | 1.4a | The two roles: the ECS agent's execution role and the gateway process's task role |

`tests/module.tftest.hcl` and `tests/network.tftest.hcl` check it against a mock
provider, with no credentials and no network beyond `terraform init`. The Terraform
test step in CI discovers both.

## Names and tags

Resources are named `spa-platform-{cell}` and its derivatives, matching
`infra/bootstrap`'s `name_prefix`. The exceptions come from
`contracts/infra-outputs.example.json`, which fixes them: the bucket is
`spa-platform-{cell}`, the image repositories are `spa-{cell}/gateway` and
`spa-{cell}/mock-api`, and the load balancer and its target group are `spa-{cell}`.
The last is also forced: both names are capped at 32 characters, and
`spa-platform-{cell}` reaches 35 at the cell name's maximum length.

The module sets no provider and so no tags. The calling root applies `Platform`,
`Cell` and `ManagedBy` through `default_tags`, as `contracts/resource-tags.md`
requires; resources here add only `Name`, which merges with those three.

## What the network does not have

- **No NAT gateway.** The task sends 443 out from a public address. A NAT gateway
  costs more per month than everything else in the cell together.
- **No ingress from the internet.** The load balancer is internal and accepts 443
  from the task security group alone, which is the Session Manager tunnel's path,
  and the task security group accepts the gateway's port from the ALB security group
  alone. Those are two of the cell's four rules. `tests/network.tftest.hcl` fails on
  a fifth rule, an inline rule, a third group, or an address range on any rule but
  the task's 443 out, and the resource-type allowlist in `tests/module.tftest.hcl`
  fails on a NAT gateway, an instance or a database. Both read the configuration
  (ADR-0016). P-06's deployed half, a connection from outside to the task's port
  timing out, runs against the applied cell at G3 (task 6.9).
- **No rule in the default security group.** AWS creates one in every VPC, admitting
  all traffic from its own members, and puts some resources created without a group of
  their own into it, an EC2 instance for one. The module adopts it as
  `aws_default_security_group.cell` with both rule lists empty: the first apply
  strips it, and a rule added by hand later shows in the next plan.
- **No Local Zone.** The two subnets go in the first two zones by name, and the zone
  query asks only for zones that need no opt-in. A Local Zone's name sorts ahead of
  every Availability Zone in its Region, so without that filter, opting into one
  would move both subnets.
- **No VPC endpoint, flow log or IPv6 range** in Phase 1 (ticket 03, question 7).
  The task's 443 goes out through its public address, and the S3 gateway endpoint
  belongs with Phase 2's private subnets.

Every rule and group description keeps to the characters EC2 accepts, which exclude
the apostrophe. EC2 refuses any other at apply, where no plan reaches.

## One gap the shared subnets leave

The task sits in the load balancer's two subnets, so its own address falls inside
`ForwardedHeaders:TrustedNetworks`, which names those subnets. Whoever can open a
Session Manager session on the task can forward a port to the task's own address on
the gateway's port. That connection never leaves the task, so no security group sees
it, and the gateway trusts the `X-Forwarded-For` and `X-Forwarded-Proto` it carries.
A forward to 127.0.0.1 is not trusted, because the gateway clears the framework's
loopback proxies (`src/Platform.Security/PlatformForwardedHeaders.cs`). Only operators
hold that session, and they can already run commands in the task. Phase 1 accepts
this (ticket 03, question 5); it closes when Phase 2 moves the task to private
subnets (P2-4) and drops ECS Exec (P2-9), both in `docs/plan/06-phase-2-and-risks.md`.

## The load balancer

One listener, one target group, no listener rules (ADR-0010). Every host reaches the
gateway, which resolves the app by exact authority match.

- **Internal, HTTPS on 443 only.** No port 80 listener and no redirect: only the
  tunnel reaches it. This module pins a TLS 1.2/1.3 policy as an implementation choice.
- **Host header preserved, invalid header fields dropped.** The gateway reads the
  `Host` the browser sent, port included.
- **The certificate is found, not created.** `tools/scripts/certs.sh import` puts
  `*.{certificate_domain}` into ACM tagged with this cell, and `alb.tf` looks it up by
  that domain, the `Cell` tag and the key types `certs.sh` searches. Run it before the
  first apply, or the plan fails on the lookup.
- **The health-check path follows the image.** `health_check_path` is `/` for the
  sample image and `/readyz` for the gateway.

## The compute, and what a green plan does not say

Task 1.4a wrote the ECS half and proved everything about it a plan can prove. It did
not prove row 1.4's own done-when — a steady service, a healthy target, a successful
image pull or a working tunnel — because each of those needs the apply, and the apply
needs credentials, a state bucket and the first-deployment order in
`docs/plan/05-infrastructure-and-cost.md`. **A green plan here is not a running cell.**

- **The task.** ARM64, 0.25 vCPU, 1 GB, Fargate only, in this cell's two public
  subnets with a public address, because the no-NAT design leaves that as the task's
  only route out. ECS Exec is on and the root filesystem stays writable: those are two
  of the three conditions the Session Manager tunnel comes with, and the third is the
  four `ssmmessages` permissions below.
- **The deployment.** One service, desired count 1, circuit breaker on with rollback.
  Both images are variables with no default that names a tag: `deploy.sh` passes a
  digest and every other apply passes the digest already in state, which is what keeps
  a routine apply from moving a running image. `mock_api_image` defaults to absent, so
  the service applies before task 5.3's image exists.
- **The task environment** is generated from `contracts/platform-config.schema.json`
  in the form that contract names: a colon becomes a double underscore and every value
  is a string. The bucket, the registry key, the poll interval and the cell name come
  from this module's own bucket and variables, and `TrustedNetworks` names only the ALB
  subnet ranges, so stage 1 trusts the load balancer and nothing wider.

### The task role is the security object of record

It is the one object in the cell where a careless grant is easy to write and hard to
notice, which is why it was written and reviewed before the AWS sitting rather than
during it. It holds exactly three grants, each asserted on its own:

| Grant | Scope |
| --- | --- |
| `ReadRegistryAndReleases` | `s3:GetObject` on this bucket's `registry/*` and `apps/*`, the two prefixes `contracts/s3-layout.md` lays out. No write, no delete, no listing, and not the bucket's own ARN |
| `ReadClientSecrets` | `ssm:GetParameter` and `ssm:GetParameters` on `/spa-platform/{cell}/apps/*`. Another cell's secret is outside it |
| `SessionManagerMessages` | The four `ssmmessages` actions the tunnel needs. They take no resource-level condition, so this is the one grant on `*` — and the test asserts it is the only one |

A fourth statement fails a test. So does a wildcard in an action, a `Deny`, a
`NotAction` or `NotResource`, and a second grant on `*`: the suite names the statements
it recognises and fails on one it does not, so a widened grant cannot pass by being
unmentioned. The execution role is separate and holds image pull and log writes only;
neither role can do the other's job.

## The two things the bucket enforces that no code does

Both are in `contracts/s3-layout.md`, and test P-04 proves them against the deployed
bucket.

- A write under `apps/` that carries no `If-None-Match` header is refused, so a
  release prefix is written once.
- A delete under `apps/` is refused to everyone but `teardown_principal_arns`,
  because a delete followed by a write would otherwise replace a release.

`teardown_principal_arns` has no default and refuses to be empty: a bucket nobody may
empty cannot be destroyed, and P-05 would then fail for ever. The platform root fills
it with the teardown role `infra/bootstrap` creates, `spa-platform-<cell>-teardown`,
looked up by that derived name, followed by any `additional_teardown_principal_arns`
(change request 1.5, ADR-0014). Bootstrap grants that role the delete on
`spa-platform-<cell>`, this module's default bucket name; override `bucket_name` only
together with bootstrap's `platform_bucket_name`.

The permission and resource assertions run at plan time. One additional mock-only apply run checks
that each grant and trust document is attached to its intended role. Mock resources exist only in
Terraform's in-memory test state; this does not apply the infrastructure to AWS.
