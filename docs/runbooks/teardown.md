# Tear the cell down

Destroy everything the cell is, and prove nothing is left. `docs/plan/05-infrastructure-and-cost.md`,
Teardown, and test P-05: "a query for the platform tag returns no resources". Task 6.9 exercises this
note; it tears down and deploys again.

**Nothing here can be undone.** Every release, every registry version and every session goes, and
every user is signed out. Destroying the cell between sessions brings its cost from about $30 a month
to cents (`docs/plan/05-infrastructure-and-cost.md`, Cost model).

Start with the operator session in [README.md](README.md). Your session must be able to assume the
cell's teardown role, `spa-platform-<cell>-teardown`: it is the one principal the bucket policy lets
delete a release (ADR-0014), and its trust allows only the principals in the bootstrap root's
`teardown_trusted_principal_arns`, with MFA (`infra/bootstrap/README.md`, The teardown role).

## 1. Run it

```bash
tools/scripts/teardown.sh --yes             # the poc cell
tools/scripts/teardown.sh --yes --cell poc  # or name the cell
```

In order, and it stops at the first that fails (its header has the detail):

1. It empties the platform bucket: every version and delete marker under `apps/` as the teardown
   role, everything else as you. The image repositories are `force_delete`, so the destroy removes
   their images.
2. `terraform destroy` in `infra/envs/poc/platform`, then `apps`, then `idp`. `images.auto.tfvars`
   goes with the platform root, so the next first deployment starts on the sample image.
3. It deletes the certificate `certs.sh` imported, retrying while ACM still counts it in use, and the
   cell's inactive task definitions, which keep their tags until deleted.
4. It asks the tagging API for every resource whose `Platform` tag is exactly `spa-platform`
   (`contracts/resource-tags.md`), retrying for up to five minutes because the API can lag a
   deletion. Any resource still tagged fails the script and is named, with the account id replaced.

Exit 0 means the tag query answered nothing. A failed step stops it with nothing after it run; fix
the cause and run it again. A root already destroyed has nothing left in its state to destroy, and
a platform root with no `bucket` output has no bucket to empty, which the script says and moves on.

## 2. Prove the IAM roles gone by name

The tag query cannot list IAM roles, and the cell's two roles carry the platform tag (#118), so exit
0 does not show them gone. Until #118 decides how P-05 and `teardown.sh` prove them, check them by
name (`infra/modules/cell/iam.tf`):

```bash
for role in spa-platform-poc-task spa-platform-poc-task-execution; do
  answer="$(aws iam get-role --role-name "$role" --query 'Role.RoleName' --output text 2>&1)"
  case "$answer" in
    *NoSuchEntity*) echo "$role: gone" ;;
    *) echo "$role: STILL PRESENT, or the check failed (the message is not printed: it can carry an ARN)" ;;
  esac
done
```

Both lines must say `gone`.

## 3. What stays, on purpose

The bootstrap root's three resources, the Terraform state bucket, the CI plan role and the teardown
role, carry `Platform = spa-platform-bootstrap` and do not answer the query. They outlive every
teardown by design: the next first deployment needs them, and destroying the state bucket from inside
a teardown would destroy the state of the roots being destroyed (`contracts/resource-tags.md`, The
bootstrap exemption).

They are removed by hand, and only when the cell will never be built again in this account. The
bootstrap root keeps local state on the workstation that applied it (`infra/bootstrap/README.md`), so
that is where its removal runs from.

## 4. Build it again

[deploy.md](deploy.md), The first deployment of a new cell. With the bootstrap and the workstation's
certificate authority kept, step 1 is only `tools/scripts/certs.sh import`, since step 3 above
deleted the certificate from ACM and the listener finds it there by its domain name.
