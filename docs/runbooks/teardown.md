# Tear the cell down

Destroy everything the cell is, and prove nothing is left: at the end, a query for the platform tag
returns no resources.

**Nothing here can be undone.** Every release, every registry version and every session goes, and
every user is signed out. Destroying a cell between uses brings its cost to cents.

Start with the operator session in [README.md](README.md). Your session must be able to assume the
cell's teardown role, `spa-platform-<cell>-teardown` with `<cell>` the entry file's `$SPA_CELL`: it is
the one principal the bucket policy lets delete a release (ADR-0014).

## 1. Run it

```bash
tools/scripts/teardown.sh --yes                       # the entry file's cell, $SPA_CELL
tools/scripts/teardown.sh --yes --cell "$SPA_CELL"    # or name the cell
```

In order, and it stops at the first that fails (its header has the detail):

1. It empties the platform bucket: every version and delete marker under `apps/` as the teardown
   role, everything else as you. The image repositories are `force_delete`, so the destroy removes
   their images.
2. `terraform destroy` in each root on `$SPA_ROOTS`, last first, the reverse of the order they are
   applied in. Every `*.auto.tfvars` in the platform root, `$SPA_PLATFORM_ROOT`, goes with it,
   `images.auto.tfvars` among them, so the cell's next build starts on the sample image.
3. It deletes the cell's imported certificate, retrying while ACM still counts it in use, the
   cell's inactive task definitions, which keep their tags until deleted, and the key ring the
   gateway wrote, which no root's state holds.
4. It asks the tagging API for every resource whose `Platform` tag is exactly `spa-platform`
   (`contracts/resource-tags.md`), retrying for up to five minutes because the API can lag a
   deletion. Any resource still tagged fails the script and is named, with the account id replaced.

Exit 0 means the tag query answered nothing. A failed step stops it with nothing after it run; fix
the cause and run it again. A root already destroyed has nothing left in its state to destroy, and
a platform root with no `bucket` output has no bucket to empty, which the script says and moves on.

## 2. Prove the IAM roles gone by name

The tag query cannot list IAM roles, though the cell's two roles carry the platform tag, so the
query's answer does not show them gone. Check them by name:

```bash
for role in "spa-platform-$SPA_CELL-task" "spa-platform-$SPA_CELL-task-execution"; do
  answer="$(aws iam get-role --role-name "$role" --query 'Role.RoleName' --output text 2>&1)"
  case "$answer" in
    *NoSuchEntity*) echo "$role: gone" ;;
    *) echo "$role: STILL PRESENT, or the check failed (the message is not printed: it can carry an ARN)" ;;
  esac
done
```

Both lines must say `gone`.
