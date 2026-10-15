# ADR-0014: Create the teardown role in `infra/bootstrap`, and derive its name from the cell

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** integrator
- **Plan ref:** No decision row. `docs/plan/02-contracts.md` → S3 layout and
  `docs/plan/03-workstreams-and-tasks.md` → task 1.5 both name "the teardown role" without saying
  who creates it. Raised as change request
  [`contracts/changes/1.5-teardown-role-and-ignored-tfvars.md`](../../contracts/changes/1.5-teardown-role-and-ignored-tfvars.md)
  by workstream 1.
  **This ADR amends a plan boundary.** `docs/plan/05-infrastructure-and-cost.md` → Terraform
  boundaries gives `infra/bootstrap` "Never contains: Anything else", and
  `infra/bootstrap/README.md` cites that row. `docs/plan/` is a frozen export, so the amendment can
  only live here: bootstrap contains the state bucket, the read-only CI plan role **and the teardown
  role**, and nothing else.

## Context

ADR-0009 makes a release prefix write-once with two halves of one bucket policy: a `Deny` on
`s3:PutObject` under `apps/` without `If-None-Match`, and a `Deny` on `s3:DeleteObject` and
`s3:DeleteObjectVersion` there for every principal **except the teardown role**. Without the second
half a release could be replaced in two steps, which is the whole point of P-04. ADR-0009's own
Consequences say so: "Without the delete rule, a delete followed by a write would replace a
release."

So the policy's `Deny` has an exception, and nothing in the platform defines who takes it.
`infra/bootstrap` creates the state bucket and the read-only CI plan role and nothing else;
`contracts/infra-outputs.schema.json` has no field for it. Task 1.5 shipped
`modules/cell` → `teardown_principal_arns`, a list with no default and a validation that refuses an
empty one, so an apply fails loudly rather than creating a bucket nobody can empty. That is a
placeholder: it puts the value behind a frozen contract's rule into an uncommitted
`terraform.tfvars`, where it lives only on whichever workstation applied last.

Both failure modes are sharp. Name no principal and nothing can ever delete under `apps/`:
`teardown.sh` cannot empty the bucket, `terraform destroy` fails, and P-05 can never pass. Name the
wrong one and a release can be replaced in two steps.

**The wrong ARN is easy to name.** `aws:PrincipalArn` holds the **role** ARN for a request made by a
role session, and the IAM documentation says in as many words: "For IAM roles, the request context
returns the ARN of the role, not the ARN of the user that assumed the role… Do not specify the
assumed role session ARN as a value for this condition key." But `aws sts get-caller-identity`
prints the session ARN, `arn:aws:sts::<account>:assumed-role/<role>/<session>`. An operator asked
for "your ARN" pastes that, the `ArnNotLike` never matches it, the deny applies to the teardown
principal too, and the bucket cannot be emptied — discovered at teardown, at G3, after the stack is
up. That alone decides against leaving this to a hand-filled variable.

**And a derived ARN has the same failure mode, silently.** The ARN appears in a policy *condition*,
not a `Principal` block, so AWS validates nothing: a name that matches no role, or a resource glob
that matches no bucket, applies cleanly and fails only at teardown. Every constant in this decision
is therefore either looked up from AWS or asserted at plan time.

## Decision

**The teardown role is `spa-platform-<cell>-teardown`, created by `infra/bootstrap`.**

This is an accepted implementation decision, not evidence that the Terraform below has landed.
At this PR's base `070e3fae`, bootstrap has no teardown role and the cell module defaults its bucket
name to `spa-platform-<cell>` (with a `bucket_name` override). Workstream 1 must implement the
follow-ups in change request 1.5 before an apply relies on this convention. The account-qualified
bucket name below is the intended default; permissions must use the actual bucket ARN, including
any supported override. In particular, `spa-platform-<cell>-*` does match the intended
account-qualified name; it misses the current unsuffixed default and is broader than one bucket.

1. **It lives in `infra/bootstrap`** beside the CI plan role. A teardown role cannot live in a root
   that teardown destroys, and bootstrap's resources already outlive every teardown. It carries
   bootstrap's tags — `Platform = spa-platform-bootstrap`, `Cell`, `Component = bootstrap`, `Root` —
   so it does not answer the platform query and P-05 stays a proof (ADR-0013).
2. **Its name is derived; its ARN comes from AWS.** The name follows `local.name_prefix`
   (ADR-0012), so it is `spa-platform-<cell>-teardown` and every consumer computes it from values it
   already has. The platform root derives the **name** and then looks the role up —
   `data "aws_iam_role" "teardown"` — and passes the ARN AWS returns to `modules/cell`. A missing or
   mis-derived role is then a plan error rather than a G3 discovery, and `iam:GetRole` is already
   granted on this name. The landed root test takes a `mock_data` block, as the sibling idp and apps
   roots already do; the known limit is that the mock satisfies the lookup, so the property holds
   against AWS and not in `terraform test`. `teardown.sh` derives the same name without reading any
   state. `modules/cell` keeps `teardown_principal_arns` and its non-empty validation unchanged,
   because a module that takes the principals as input is right and only the root's defaulting was
   missing.
3. **An extra principal is visibly additional.** The platform root's variable is
   `additional_teardown_principal_arns`, default `[]`, concatenated with the derived one. A variable
   that *replaced* the derived ARN would be a way to lock out the only principal that can empty the
   bucket, and because the ARN sits in a condition the apply would not notice.
4. **The value is the role ARN, never the assumed-role session ARN.** `contracts/s3-layout.md` says
   so, because three parties have to agree on the string.
5. **Trust is narrow, human, and required.** `infra/bootstrap` takes
   `teardown_trusted_principal_arns` — its **own** variable, not the CI role's
   `additional_trusted_principal_arns`, because the two lists are different populations: read
   everything, versus delete releases. It has no default and is guarded by a precondition that fails
   the apply when nobody is named, the way `trust_is_configured` already guards the CI role. The
   humans cannot be derived and `*.tfvars` is now ignored, so a loud failure is the only safe shape.
   The role is not assumable by the CI plan role and not through the GitHub Actions OIDC provider:
   CI plans; it never deletes. Validate the input as explicit IAM user or role ARNs, rejecting
   account-root and STS session ARNs. IAM-user trust requires `Allow` with
   `Bool: {"aws:MultiFactorAuthPresent": "true"}`, so long-term keys alone cannot assume it.
   Federated human roles need a separate, explicitly allowlisted trust statement and MFA enforced
   by their identity provider: the MFA context key can be absent for those sessions and cannot
   attest to their MFA. `Allow` with `BoolIfExists: true` would also admit long-term keys;
   `Deny` with `BoolIfExists: false` would block both those keys and missing-key federation.
   Neither combination alone provides both guarantees. AWS need not reject a session ARN in a
   trust policy, so the Terraform input validation must reject it before apply.
6. **Its permissions are the delete and nothing else**, scoped to real ARNs rather than a glob:
   `s3:DeleteObject` and `s3:DeleteObjectVersion` on `<bucket ARN>/apps/*`, and `s3:ListBucket` and
   `s3:ListBucketVersions` on the bucket ARN, where the bucket is
   intended to default to `spa-platform-<cell>-<account id>`; use the actual bucket ARN rather than
   reconstructing that string. Destroying the three roots runs as the operator's own
   principal, not as this role.
7. **The cell name is validated identically in both roots:** 3 to 22 characters,
   `^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$`, no leading or trailing hyphen, with a comment naming where
   each bound comes from. The lower bound is the frozen contracts' appId shape; the upper is the
   bucket and load-balancer names, and a landed test on trunk pins 22. This **widens** bootstrap
   rather than tightening `modules/cell`.
8. **It is not added to `contracts/infra-outputs.schema.json`.** Bootstrap outputs
   `teardown_role_arn` for humans, and the derivation stays normative for every consumer. If any
   consumer reads that output, option 2 is back through the side door.

`.gitignore` also gains `*.tfvars`, `*.tfvars.json` and `backend.hcl`, which is the change request's
second half and needs no architecture.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| A field in `contracts/infra-outputs.schema.json`, the change request's option 2 | `outputs.json` is written *after* an apply, and teardown needs the role when outputs are stale or already deleted. Reading a teardown constant out of an apply artefact is circular — ADR-0013 rejected the same shape for the platform tag. It would also be a new required field in a contract frozen at G1a, which `contracts/README.md` sends to the next gate rather than to a decision. |
| "It is the operator's own principal by design", the change request's option 3 | Leaves a frozen contract's `Deny` exception in an uncommitted file on one workstation, and walks straight into the assumed-role-session-ARN trap above. A rule this sharp should be reproducible, not remembered. |
| A role in the platform root, beside the bucket it exempts | It would be destroyed by the teardown it exists to permit. The last `terraform destroy` would remove the role and then fail on the objects it could no longer delete. |
| One role per account rather than per cell | Breaks ADR-0012: a second cell's teardown would then be exempt on the first cell's bucket. |
| Assembling the ARN from `aws_caller_identity` and `aws_partition` instead of looking the role up | The assembled string is never checked against anything. Because it lands in a condition, a typo or a renamed role applies cleanly and is found at teardown. |
| Give the teardown role the conditional-write exemption too, so it can repair a prefix | That is exactly the two-step replacement ADR-0009 forbids. The role deletes; it never writes. |

## Consequences

- Task 1.5's placeholder becomes an implementation: workstream 1 adds the role to `infra/bootstrap`,
  defaults the platform root's principals from the convention, and the change request closes.
  Nothing already applied has to move, because nothing has been applied.
- **The role is break-glass, and narrow by separation of powers, not by the write rule.** Whoever
  can assume it can delete a published release. They cannot replace one **because no principal holds
  both the delete and the write** — not because a conditional write would refuse the second step.
  S3's documented behaviour is the opposite: a conditional write **succeeds** when the current
  version is a delete marker, so a principal holding both would replace a release in two steps. This
  is the sentence a future reader would otherwise cite when asked to give the teardown role a write
  "to repair a prefix".
  This is separation between credentials, not tamper-proof retention: a human who can assume the
  teardown role and then write with another principal can still delete and republish. Teardown
  access therefore remains trusted break-glass access; P-04 proves the ordinary writer's denial.
- **P-04 must not run under the teardown role**, or it would observe the exception rather than the
  rule. It runs as the test's own principal, which is denied, which is what it asserts. P-04 stays
  negative-only; the positive half is proved by task 6.9's tear-down-and-deploy-again.
- **Every release is kept for the life of the cell**, because pruning is now break-glass only. At the
  plan's storage figures that is under $0.50, so it is a cost decision already taken.
- **No lifecycle or replication rule may be added to the platform bucket.** `aws:PrincipalArn` is
  absent for S3's own expiration, so a lifecycle rule would delete "immutable" releases while P-04
  still passed.
- `teardown.sh` (task 1.7) **switches principals**: it assumes the teardown role to empty `apps/`,
  then destroys the roots as the operator. The plan gives that script five jobs and the role may do
  exactly one, so "assume the role, then run teardown" fails four steps in. It must empty `apps/`
  with `s3api list-object-versions` and `s3api delete-objects`: the high-level `aws s3 rm` may need
  `GetBucketLocation` and cannot remove noncurrent versions or delete markers at all. The script
  derives the name and reads no state, which matters because **bootstrap keeps local state, so its
  outputs exist only on the workstation that applied it.**
- The platform bucket's `force_destroy` stays **off**, and not as an oversight: it deletes objects as
  the operator, whom the policy denies under `apps/`, so it would turn a destroy into `AccessDenied`.
- A second cell gets its own role from its own bootstrap apply, with no shared name to collide.
- Bootstrap now creates three things, not two. Both `infra/bootstrap/README.md` ("owns two things and
  nothing else") and `contracts/resource-tags.md` — which names the exempt set as "two resources" in
  three places — must say three. The tags contract is the file workstream 6 is told to read rather
  than infer from Terraform, so left alone it would answer "is the teardown role exempt?" wrongly.

## Evidence

- `aws:PrincipalArn` holds the role ARN for a role session, and the assumed-role session ARN must
  not be used in that condition key: [IAM User Guide, AWS global condition context
  keys](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html),
  read 14 October 2026.
- MFA operator semantics and missing-key federation: the same IAM reference's
  [`aws:MultiFactorAuthPresent` section](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html#condition-keys-multifactorauthpresent),
  checked 15 October 2026. User and federated-role trust must be evaluated separately.
- A conditional write succeeds when the current object version is a delete marker:
  [Amazon S3 User Guide, conditional
  requests](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-requests.html), read
  15 October 2026. Corroborated by ADR-0009's own Consequences.
- The two halves of the bucket policy as task 1.5 shipped them:
  `infra/modules/cell/storage.tf`, `DenyReleaseWriteWithoutIfNoneMatch` and
  `DenyReleaseDeleteExceptTeardown`.
- The naming convention and the bootstrap tag exemption: ADR-0012, ADR-0013,
  `contracts/resource-tags.md`.
- The 22-character cell bound: the landed `cell = "abcdefghijklmnopqrstuv"` case in the platform
  root's test file.
