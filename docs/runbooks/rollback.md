# Roll the gateway back

Put the previous gateway image back: `rollback.sh` applies the gateway image of the previous task
definition. The deployment circuit breaker also rolls back on failed health checks.

**It signs out whom a deployment signs out**, since it is one: on one task with sessions in memory,
every user of every app; on two tasks with the durable session store, no one ([deploy.md](deploy.md)
says why).

Start with the operator session in [README.md](README.md).

## First, is it needed?

ECS's deployment circuit breaker already rolls back a deployment whose tasks fail their health
checks. Look at what the service runs before rolling anything back:

```bash
CLUSTER="$(python3 -I tools/scripts/cell_documents.py field "$SPA_OUTPUTS_FILE" ecsCluster "$AWS_REGION")"
SERVICE="$(python3 -I tools/scripts/cell_documents.py field "$SPA_OUTPUTS_FILE" ecsService "$AWS_REGION")"
aws ecs describe-services --region "$AWS_REGION" --cluster "$CLUSTER" --services "$SERVICE" \
  --query 'services[0].deployments[].[status, rolloutState, taskDefinition]' --output text | sed 's#arn:.*task-definition/##'
```

The last command prints each deployment's family:revision without the ARN, which carries the
account id. `rollback.sh` is for a gateway that runs steady and healthy and is still the wrong one.

## Roll back

```bash
tools/scripts/rollback.sh                    # the previous gateway image
tools/scripts/rollback.sh --to-revision 7    # the gateway image of revision 7 of the task definition family
```

The previous gateway is the newest revision below the running one whose gateway image differs: an
apply that changed something else made a revision with the same image, and going back to it would
change nothing. It moves the gateway's image only: an image the cell runs beside the gateway, a
mock API for one, stays at what the platform root's tfvars hold, whatever that revision ran. The
script looks back twenty revisions.

It refuses, with exit 1:

- a revision whose gateway is not a build from the cell's gateway repository. The sample image of
  the first apply answers `/` and writes under `/tmp` on start-up. Returning to it requires a
  platform-root apply with the sample's pinned `gateway_image`, `health_check_path = "/"` and
  `gateway_writable_root = true`. If `images.auto.tfvars` exists, update those values there
  before applying: Terraform loads it after `terraform.tfvars`;
- an image the repository no longer holds, since the task could not pull it;
- `--to-revision` naming the revision already running, or one ECS no longer describes;
- without `--to-revision`, when none of the twenty revisions below the running one has another
  gateway image: there is nothing to roll back to.

Then it does what `deploy.sh` does after its push: it applies the platform root,
`$SPA_PLATFORM_ROOT`, with that digest and the `/readyz` health check, records them in
`images.auto.tfvars`, rewrites the outputs document, `$SPA_OUTPUTS_FILE`, waits for a stable
service and asserts the running digest and the targets' health as `deploy.sh`'s step 5 does,
tolerating the previous task's draining target ([deploy.md](deploy.md), steps 3 to 5). It stops
there: the readiness wait that ends `deploy.sh`'s step 5, reading each task's revision through a
tunnel of its own, is not part of a rollback, so `rollback.sh` opens no tunnel, needs no Session
Manager plugin and needs no tunnel running.

Afterwards, check it yourself through `tools/scripts/tunnel.sh`. Its hop is the cell's bastion task
([README.md](README.md), "Waiting for the gateway: the tunnel"), which a rollback does not replace,
so a tunnel started before it can still be up; if it has ended, start it again. Then:

```bash
tools/scripts/verify-tunnel.sh /healthz
```

A later `deploy.sh` builds from the working tree again, so revert or fix the change that made the
rollback necessary before the next deployment.
