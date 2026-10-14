# `modules/cell`

One cell: the deployment unit. A Phase 2 cell is one more instance of this module
(ADR-0012), which is why nothing here is named for the proof of concept and every
name carries `var.cell`.

## What is here

| File | Task | Holds |
| --- | --- | --- |
| `network.tf` | 1.2 | The VPC, two public subnets in two zones, the internet gateway, one route table, and the two security groups |
| `storage.tf` | 1.5 | The platform bucket and its policy, and the two image repositories |

Tasks 1.3 and 1.4 add the load balancer, the listener and the target group, and then
the ECS cluster, task definition, service and log group, to this same module.

## Names and tags

Resources are named `spa-platform-{cell}` and its derivatives, matching
`infra/bootstrap`'s `name_prefix`. The two exceptions come from
`contracts/infra-outputs.example.json`, which fixes them: the bucket is
`spa-platform-{cell}` and the image repositories are `spa-{cell}/gateway` and
`spa-{cell}/mock-api`.

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
