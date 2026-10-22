# Roll the gateway back

Put the previous gateway image back. The Gateway lever's rollback in
`docs/plan/05-infrastructure-and-cost.md`, Deployment and rollback: "`rollback.sh` applies the
digest of the previous task definition. The deployment circuit breaker also rolls back on failed
health checks."

**It signs every user of every app out**, as a deployment does: it is one.

Start with the operator session in [README.md](README.md).

## First, is it needed?

ECS's deployment circuit breaker already rolls back a deployment whose tasks fail their health
checks. Look at what the service runs before rolling anything back:

```bash
CLUSTER="$(python3 -I tools/scripts/cell_documents.py field infra/envs/poc/outputs.json ecsCluster "$AWS_REGION")"
SERVICE="$(python3 -I tools/scripts/cell_documents.py field infra/envs/poc/outputs.json ecsService "$AWS_REGION")"
aws ecs describe-services --region "$AWS_REGION" --cluster "$CLUSTER" --services "$SERVICE" \
  --query 'services[0].deployments[].[status, rolloutState, taskDefinition]' --output text | sed 's#arn:.*task-definition/##'
```

The last command prints each deployment's family:revision without the ARN, which carries the
account id. `rollback.sh` is for a gateway that runs steady and healthy and is still the wrong one.

## Roll back

```bash
tools/scripts/rollback.sh                    # the previous gateway image
tools/scripts/rollback.sh --to-revision 7    # the images of revision 7 of the task definition family
```

The previous gateway is the newest revision below the running one whose gateway image differs: an
apply that changed something else made a revision with the same image, and going back to it would
change nothing. The mock API image of that revision comes with it, since the two were deployed
together. The script looks back twenty revisions.

It refuses, with exit 1:

- a revision whose gateway is not a build from the cell's gateway repository. The sample image of
  the first apply answers `/` and not `/readyz`, so going back to it is a platform-root apply with
  `health_check_path = "/"`, not a rollback;
- an image the repository no longer holds, since the task could not pull it;
- `--to-revision` naming the revision already running, or one ECS no longer describes;
- without `--to-revision`, when none of the twenty revisions below the running one has another
  gateway image: there is nothing to roll back to.

Then it does what `deploy.sh` does after its push: it applies the platform root with those digests
and the `/readyz` health check, records them in `images.auto.tfvars`, rewrites
`infra/envs/poc/outputs.json`, waits for a stable service and asserts the running digest and the
target's health ([deploy.md](deploy.md), steps 3 to 5).

Afterwards, start `tools/scripts/tunnel.sh` again (its hop was the replaced task), then:

```bash
tools/scripts/verify-tunnel.sh /healthz
```

A later `deploy.sh` builds from the working tree again, so revert or fix the change that made the
rollback necessary before the next deployment.
