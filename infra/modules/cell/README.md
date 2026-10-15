# `modules/cell`

One cell: the deployment unit. A Phase 2 cell is one more instance of this module
(ADR-0012), which is why nothing here is named for the proof of concept and every
name carries `var.cell`.

## What is here

| File | Task | Holds |
| --- | --- | --- |
| `network.tf` | 1.2 | The VPC, two public subnets in two zones, the internet gateway, one route table, and the two security groups |
| `storage.tf` | 1.5 | The platform bucket and its policy, and the two image repositories |
| `alb.tf` | 1.3 | The internal load balancer, its one HTTPS listener, its one target group, and the lookup of the imported wildcard certificate |

Task 1.4 adds the ECS cluster, task definition, service and log group to this same
module.

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

## The two things the network does not have

- **No NAT gateway.** The task sends 443 out from a public address. A NAT gateway
  costs more per month than everything else in the cell together.
- **No ingress from the internet.** The load balancer is internal and accepts 443
  from the task group alone, which is the Session Manager tunnel's path. Test P-01
  reads the plan and fails on a NAT gateway, a bastion, a database or any other
  ingress rule; P-06 shows a connection from outside to the task's port timing out.

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

## The two things the bucket enforces that no code does

Both are in `contracts/s3-layout.md`, and test P-04 proves them against the deployed
bucket.

- A write under `apps/` that carries no `If-None-Match` header is refused, so a
  release prefix is written once.
- A delete under `apps/` is refused to everyone but `teardown_principal_arns`,
  because a delete followed by a write would otherwise replace a release.

`teardown_principal_arns` has no default and refuses to be empty: a bucket nobody may
empty cannot be destroyed, and P-05 would then fail for ever. Change request 1.5 asks
the integrator where that ARN should come from, because no root creates a teardown
role today.
